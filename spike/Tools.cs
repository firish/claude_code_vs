using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// One handler per protocol tool. The real extension will have an identical shape in src/Tools/,
/// just backed by VS SDK calls instead of stubs. Return a plain string to send it verbatim on the
/// wire (e.g. "DIFF_ACCEPTED"); return any other object to have it JSON-stringified. Throw to
/// surface an MCP error (isError=true).
/// </summary>
internal interface IIdeTool
{
    string Name { get; }

    /// <summary>The JSON Schema advertised in tools/list. Becomes the mcp__ide__* tool the model sees.</summary>
    JsonNode Schema { get; }

    Task<object> InvokeAsync(JsonElement arguments, CancellationToken ct);
}

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, IIdeTool> _tools;

    public ToolRegistry(IEnumerable<IIdeTool> tools)
        => _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IEnumerable<IIdeTool> All => _tools.Values;

    public bool TryGet(string name, out IIdeTool tool) => _tools.TryGetValue(name, out tool!);
}

// ---------------------------------------------------------------------------
// Deferred-decision coordinator for openDiff. The tool call must NOT return until
// the user accepts/rejects, so we park a TaskCompletionSource keyed by tab_name and
// complete it later - from a console keypress, or auto-accept in --auto-accept-diffs mode.
// ---------------------------------------------------------------------------
internal sealed class DiffDecisions
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly ConcurrentQueue<string> _order = new(); // FIFO so console a/r hits the oldest

    public Task<bool> AwaitDecisionAsync(string tabName)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tabName] = tcs;
        _order.Enqueue(tabName);
        return tcs.Task;
    }

    public bool Resolve(string tabName, bool accepted)
    {
        if (_pending.TryRemove(tabName, out var tcs))
            return tcs.TrySetResult(accepted);
        return false;
    }

    /// <summary>Resolve the oldest still-pending diff (used by the console a/r keys).</summary>
    public bool ResolveOldest(bool accepted)
    {
        while (_order.TryDequeue(out var tab))
        {
            if (_pending.ContainsKey(tab))
                return Resolve(tab, accepted);
        }
        return false;
    }

    public int PendingCount => _pending.Count;
}

// ---------------------------------------------------------------------------
// openDiff - the centerpiece. In the spike we just write the proposed contents to a
// temp file (the gotcha: new_file_contents is in-memory) and block on the decision.
// The real extension renders IVsDifferenceService + an Accept/Reject InfoBar here.
// ---------------------------------------------------------------------------
internal sealed class OpenDiffTool : IIdeTool
{
    private readonly DiffDecisions _decisions;
    private readonly bool _autoAccept;

    public OpenDiffTool(DiffDecisions decisions, bool autoAccept)
    {
        _decisions = decisions;
        _autoAccept = autoAccept;
    }

    public string Name => "openDiff";

    public JsonNode Schema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["old_file_path"] = new JsonObject { ["type"] = "string" },
            ["new_file_path"] = new JsonObject { ["type"] = "string" },
            ["new_file_contents"] = new JsonObject { ["type"] = "string" },
            ["tab_name"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("old_file_path", "new_file_path", "new_file_contents", "tab_name"),
    };

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var oldPath = args.GetProperty("old_file_path").GetString() ?? "";
        var newPath = args.GetProperty("new_file_path").GetString() ?? "";
        var contents = args.GetProperty("new_file_contents").GetString() ?? "";
        var tabName = args.GetProperty("tab_name").GetString() ?? Guid.NewGuid().ToString();

        // Gotcha (CLAUDE.md): new_file_contents is in-memory. Write to a temp file to feed the
        // (future) comparison view; only the real Accept path writes to new_file_path.
        var temp = Path.Combine(Path.GetTempPath(), $"claudediff_{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(temp, contents, ct);

        var decision = _decisions.AwaitDecisionAsync(tabName);

        Log.Info($"openDiff: tab='{tabName}'  old='{oldPath}'  new='{newPath}'");
        Log.Info($"         proposed contents staged at {temp} ({contents.Length} chars)");

        if (_autoAccept)
        {
            Log.Event("--auto-accept-diffs: auto-accepting in 300ms");
            _ = Task.Delay(300, ct).ContinueWith(_ => _decisions.Resolve(tabName, true), ct);
        }
        else
        {
            Log.Info($"         >>> press [a] to ACCEPT or [r] to REJECT in this console <<<");
        }

        bool accepted = await decision; // <-- BLOCKS the tool call until the user decides

        // Real extension: on accept, write `contents` to newPath via the RDT and save.
        if (accepted)
            Log.Info($"openDiff: ACCEPTED '{tabName}'");
        else
            Log.Info($"openDiff: REJECTED '{tabName}'");

        try { File.Delete(temp); } catch { /* best effort */ }

        // Plain string -> sent verbatim on the wire.
        return accepted ? "DIFF_ACCEPTED" : "DIFF_REJECTED";
    }
}

// ---------------------------------------------------------------------------
// Stubs for the rest of the core 4. Real implementations land in Phase 1.
// ---------------------------------------------------------------------------
internal sealed class OpenFileTool : IIdeTool
{
    public string Name => "openFile";

    public JsonNode Schema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["filePath"] = new JsonObject { ["type"] = "string" },
            ["preview"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["startLine"] = new JsonObject { ["type"] = "integer" },
            ["endLine"] = new JsonObject { ["type"] = "integer" },
            ["startText"] = new JsonObject { ["type"] = "string" },
            ["endText"] = new JsonObject { ["type"] = "string" },
            ["makeFrontmost"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
        },
        ["required"] = new JsonArray("filePath"),
    };

    public Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var path = args.TryGetProperty("filePath", out var p) ? p.GetString() : null;
        Log.Info($"openFile (stub): {path}");
        return Task.FromResult<object>(new { opened = true, filePath = path });
    }
}

internal sealed class GetCurrentSelectionTool : IIdeTool
{
    public string Name => "getCurrentSelection";

    public JsonNode Schema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    public Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Log.Info("getCurrentSelection (stub): returning hardcoded selection");
        // Shape mirrors what selection_changed pushes; Phase 1 reads this from IWpfTextView.Selection.
        return Task.FromResult<object>(new
        {
            success = true,
            text = "var spike = \"hello from the spike\";",
            filePath = "/Users/example/Project/Example.cs",
            fileUrl = "file:///Users/example/Project/Example.cs",
            selection = new
            {
                start = new { line = 9, character = 8 },
                end = new { line = 9, character = 42 },
            },
        });
    }
}

internal sealed class GetDiagnosticsTool : IIdeTool
{
    public string Name => "getDiagnostics";

    public JsonNode Schema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["uri"] = new JsonObject { ["type"] = "string" },
        },
    };

    public Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var uri = args.TryGetProperty("uri", out var u) ? u.GetString() : null;
        Log.Info($"getDiagnostics (stub): uri={uri ?? "(all)"} -> empty envelope");
        // ALWAYS the envelope, even when empty. Phase 1: Roslyn for .NET, Error List for C++.
        if (uri is null)
            return Task.FromResult<object>(Array.Empty<object>());
        return Task.FromResult<object>(new object[] { new { uri, diagnostics = Array.Empty<object>() } });
    }
}
