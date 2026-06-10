using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// One handler per protocol tool. In the VSIX each implementation is backed by VS SDK calls
/// (IVsDifferenceService, the Roslyn workspace, the editor adapters, ...). Return a plain string to
/// send it verbatim on the wire (e.g. "DIFF_ACCEPTED"); return any other object to have it
/// JSON-stringified; throw to surface an MCP error (isError=true). See build-plan.md §6.
/// </summary>
public interface IIdeTool
{
    string Name { get; }

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
/// Deferred-decision coordinator for openDiff. The tool call must NOT return until the user
/// accepts/rejects, so we park a TaskCompletionSource keyed by tab_name and complete it later from
/// the Accept/Reject InfoBar handlers. This is the mechanism that makes the CLI block on the user
/// (CLAUDE.md convention #3). Pure BCL, so it lives in the protocol core; the VSIX wires the UI to it.
/// </summary>
public sealed class DiffDecisions
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly ConcurrentQueue<string> _order = new(); // FIFO so a "resolve oldest" affordance hits the first-opened

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
