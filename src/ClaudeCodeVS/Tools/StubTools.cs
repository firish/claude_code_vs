using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

// ---------------------------------------------------------------------------
// Schema helpers + the parity stubs. The core 4 (openFile, openDiff, selection, diagnostics) are
// real; the remaining tools are Phase-1 stubs that keep the CLI happy.
// ---------------------------------------------------------------------------

internal static class Schemas
{
    public static JToken Empty() => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public static JToken WithFilePath(bool required = true)
    {
        var o = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["filePath"] = new JObject { ["type"] = "string" } },
        };
        if (required) o["required"] = new JArray("filePath");
        return o;
    }
}

/// <summary>Compact IIdeTool for the parity stubs — name, schema, and a function body.</summary>
internal sealed class LambdaTool : IIdeTool
{
    private readonly Func<JToken, CancellationToken, Task<object>> _fn;

    public LambdaTool(string name, JToken schema, Func<JToken, CancellationToken, Task<object>> fn)
    {
        Name = name;
        Schema = schema;
        _fn = fn;
    }

    public string Name { get; }
    public JToken Schema { get; }
    public Task<object> InvokeAsync(JToken arguments, CancellationToken ct) => _fn(arguments, ct);
}

/// <summary>
/// The remaining parity tools as Phase-1 stubs. Per build-plan §3, close_tab / closeAllDiffTabs are
/// part of the *core* diff flow (the CLI calls them right after a diff and on connect), so they return
/// success no-ops rather than errors. executeCode has no VS equivalent → honest MCP error.
/// </summary>
internal static class ParityTools
{
    public static IEnumerable<IIdeTool> All()
    {
        yield return new LambdaTool("getOpenEditors", Schemas.Empty(),
            (a, ct) => Task.FromResult<object>(new JObject { ["editors"] = new JArray() }));

        yield return new LambdaTool("getWorkspaceFolders", Schemas.Empty(),
            (a, ct) => Task.FromResult<object>(new JObject { ["folders"] = new JArray() }));

        yield return new LambdaTool("checkDocumentDirty", Schemas.WithFilePath(),
            (a, ct) => Task.FromResult<object>(new JObject { ["isDirty"] = false }));

        yield return new LambdaTool("saveDocument", Schemas.WithFilePath(),
            (a, ct) => Task.FromResult<object>("FILE_SAVED"));

        // Core diff-flow no-ops (the CLI calls these around openDiff). Plain strings -> verbatim.
        yield return new LambdaTool("close_tab",
            new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["tab_name"] = new JObject { ["type"] = "string" } },
                ["required"] = new JArray("tab_name"),
            },
            (a, ct) => Task.FromResult<object>("TAB_CLOSED"));

        yield return new LambdaTool("closeAllDiffTabs", Schemas.Empty(),
            (a, ct) => Task.FromResult<object>("CLOSED_ALL_DIFF_TABS"));

        // No VS equivalent (Jupyter kernel execution) -> honest MCP error.
        yield return new LambdaTool("executeCode",
            new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["code"] = new JObject { ["type"] = "string" } },
                ["required"] = new JArray("code"),
            },
            (a, ct) => throw new NotSupportedException("executeCode is not supported in Visual Studio"));
    }
}
