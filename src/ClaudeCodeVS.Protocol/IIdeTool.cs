using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// One handler per protocol tool. In the VSIX each implementation is backed by VS SDK calls
/// (IVsDifferenceService, the Roslyn workspace, the editor adapters, ...). Return a plain string to
/// send it verbatim on the wire (e.g. "DIFF_REJECTED"); return a <see cref="TextBlocks"/> to send
/// several text blocks (openDiff's accept); return any other object to have it JSON-stringified;
/// throw to surface an MCP error (isError=true). See build-plan.md §6.
/// </summary>
public interface IIdeTool
{
    string Name { get; }

    /// <summary>Human/model-facing description advertised in tools/list - helps the model pick the tool.</summary>
    string Description { get; }

    /// <summary>The JSON Schema advertised in tools/list. Becomes the mcp__ide__* tool the model sees.</summary>
    JToken Schema { get; }

    Task<object> InvokeAsync(JToken arguments, CancellationToken ct);
}

/// <summary>Name-keyed lookup of the registered tools. Built once at startup.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IIdeTool> _tools;

    public ToolRegistry(IEnumerable<IIdeTool> tools)
        => _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IEnumerable<IIdeTool> All => _tools.Values;

    public bool TryGet(string name, out IIdeTool tool) => _tools.TryGetValue(name, out tool!);
}

/// <summary>
/// A tool result made of several MCP text blocks instead of one. The only caller is openDiff's accept,
/// whose wire shape is positional: block 0 is the literal <c>FILE_SAVED</c> and block 1 is the file's
/// final contents, which is what the CLI then writes.
/// </summary>
public sealed class TextBlocks
{
    public TextBlocks(params string[] blocks) => Blocks = blocks;
    public IReadOnlyList<string> Blocks { get; }
}

/// <summary>The outcome of a diff review: accepted, or rejected (optionally with feedback for the CLI).</summary>
public readonly struct DiffDecision
{
    public DiffDecision(bool accepted, string? rejectReason = null, string? finalContents = null, bool userEdited = false)
    {
        Accepted = accepted;
        RejectReason = rejectReason;
        FinalContents = finalContents;
        UserEdited = userEdited;
    }

    public bool Accepted { get; }

    /// <summary>On reject, optional text the user wants Claude to act on (drives reject-with-reason).</summary>
    public string? RejectReason { get; }

    /// <summary>
    /// On accept, the text of the diff's right-hand pane at the moment of the click - Claude's proposal,
    /// or the user's edited version of it. Null when the pane could not be read (the proposal stands).
    /// </summary>
    public string? FinalContents { get; }

    /// <summary>True when <see cref="FinalContents"/> differs from what Claude proposed.</summary>
    public bool UserEdited { get; }
}

/// <summary>
/// Deferred-decision coordinator for openDiff / the permission gate. The call must NOT return until
/// the user accepts/rejects, so we park a TaskCompletionSource keyed by tab_name and complete it later
/// from the Accept/Reject UI (CLAUDE.md convention #3). Pure BCL, so it lives in the protocol core.
/// </summary>
public sealed class DiffDecisions
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffDecision>> _pending = new();
    private readonly ConcurrentQueue<string> _order = new(); // FIFO so a "resolve oldest" affordance hits the first-opened

    public Task<DiffDecision> AwaitDecisionAsync(string tabName)
    {
        var tcs = new TaskCompletionSource<DiffDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tabName] = tcs;
        _order.Enqueue(tabName);
        return tcs.Task;
    }

    public bool Resolve(string tabName, DiffDecision decision)
    {
        if (_pending.TryRemove(tabName, out var tcs))
            return tcs.TrySetResult(decision);
        return false;
    }

    /// <summary>Convenience for accept/plain-reject without a reason.</summary>
    public bool Resolve(string tabName, bool accepted) => Resolve(tabName, new DiffDecision(accepted));

    /// <summary>Resolve the oldest still-pending diff (a convenience for "accept/reject the current diff").</summary>
    public bool ResolveOldest(bool accepted)
    {
        while (_order.TryDequeue(out var tab))
        {
            if (_pending.ContainsKey(tab))
                return Resolve(tab, accepted);
        }
        return false;
    }

    public bool IsPending(string tabName) => _pending.ContainsKey(tabName);

    public int PendingCount => _pending.Count;
}
