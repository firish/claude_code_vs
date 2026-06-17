using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Debugging;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Phase 2 debug-awareness tools - the interactive PULL channel. These are exposed on the bridge's
/// secondary MCP surface (POST /mcp, reached by the stdio shim that the CLI launches as an MCP server),
/// NOT on the IDE-protocol WebSocket whose tools the CLI keeps dormant. They let the model fetch live
/// runtime state mid-turn (on demand) rather than only at prompt-submit time (the UserPromptSubmit hook).
///
/// All are read-only and marshal to the UI thread (EnvDTE is main-thread bound). Each returns a JSON
/// snapshot; when not stopped at a breakpoint the snapshot is just {"mode":"run|design|unknown"} so the
/// model can tell "nothing paused" from real data. See DebuggerReader for the EnvDTE access.
/// </summary>
internal sealed class VsDebugStateTool : IIdeTool
{
    public string Name => "vs_debug_state";
    public string Description =>
        "Get Visual Studio's live debugger state: whether it's paused, and if so the stop location, "
        + "call stack (function names, innermost first), and the current frame's arguments and locals "
        + "with their runtime values. Returns {\"mode\":\"break|run|design|unknown\", ...}. Use this to "
        + "inspect actual runtime values while debugging, not just source.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var snap = DebuggerReader.ReadSnapshot();
        var fn = (string?)snap["stoppedAt"]?["function"];
        var vals = DebuggerReader.SummarizeValues(snap);
        Log.Info($"vs_debug_state -> mode={(string?)snap["mode"]}{(fn != null ? $" @ {fn}" : "")}{(vals.Length > 0 ? $" · {vals}" : "")}");
        return snap;
    }
}

/// <summary>vs_list_breakpoints - all breakpoints (works in any mode, even before a run starts).</summary>
internal sealed class VsListBreakpointsTool : IIdeTool
{
    public string Name => "vs_list_breakpoints";
    public string Description =>
        "List all breakpoints set in Visual Studio (file, line, function, enabled, current hit count, and "
        + "condition if any). Works even when not running, so you can see where execution will stop. "
        + "Returns {\"mode\":..., \"breakpoints\":[...]}.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadBreakpoints();
        int count = (result["breakpoints"] as JArray)?.Count ?? 0;
        Log.Info($"vs_list_breakpoints -> {count} breakpoint(s)");
        return result;
    }
}

/// <summary>vs_get_frame_locals - args + locals for a specific call-stack frame (walk up to callers).</summary>
internal sealed class VsGetFrameLocalsTool : IIdeTool
{
    public string Name => "vs_get_frame_locals";
    public string Description =>
        "Get the arguments and local variables (with runtime values) for a specific call-stack frame "
        + "while paused. frameIndex 0 is the innermost/current frame; higher indices walk up toward "
        + "callers. Use vs_debug_state first to see the call stack. Break-mode only.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["frameIndex"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Call-stack frame index, 0 = innermost/current (default 0).",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadFrameLocals(frameIndex);
        var fn = (string?)result["function"];
        var vals = DebuggerReader.SummarizeValues(result);
        Log.Info($"vs_get_frame_locals(frame={frameIndex}) -> mode={(string?)result["mode"]}{(fn != null ? $" @ {fn}" : "")}{(vals.Length > 0 ? $" · {vals}" : "")}");
        return result;
    }
}

/// <summary>vs_evaluate - evaluate an expression in a chosen frame's context while paused.</summary>
internal sealed class VsEvaluateTool : IIdeTool
{
    public string Name => "vs_evaluate";
    public string Description =>
        "Evaluate an expression in the Visual Studio debugger while paused, in the context of a chosen "
        + "call-stack frame (frameIndex 0 = current). Returns {value, type, isValid}. Use it to probe "
        + "values the locals don't show (e.g. 'order.Items.Count', 'a / b', 'object.ReferenceEquals(x, y)'). "
        + "Break-mode only. IMPORTANT: the VS evaluator does NOT support LINQ or lambdas (e.g. "
        + "'list.Select(x => x.Foo)' returns isValid=false) - prefer simple expressions: indexing, "
        + "field/property access, .Count, .Sum(), arithmetic, ReferenceEquals. Note: expressions with "
        + "side effects (method calls) DO execute - there is no read-only eval.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["expression"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "The expression to evaluate, in the debugged language's syntax.",
            },
            ["frameIndex"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Frame to evaluate in, 0 = current (default 0).",
            },
        },
        ["required"] = new JArray("expression"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string expression = (string?)args["expression"] ?? "";
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.Evaluate(expression, frameIndex);

        // Log the actual result so the Output pane reads like a debug session (not just "valid=True").
        string mode = (string?)result["mode"] ?? "?";
        if (result["error"] != null)
            Log.Info($"vs_evaluate('{expression}', frame={frameIndex}) -> mode={mode}, error={(string?)result["error"]}");
        else
        {
            string val = (string?)result["value"] ?? "";
            if (val.Length > 160) val = val.Substring(0, 160) + "…";
            Log.Info($"vs_evaluate('{expression}', frame={frameIndex}) -> valid={(bool?)result["isValid"]}, value={val}");
        }
        return result;
    }
}

/// <summary>vs_expand - drill into an object's child members (Expression.DataMembers) to a depth.</summary>
internal sealed class VsExpandTool : IIdeTool
{
    public string Name => "vs_expand";
    public string Description =>
        "Expand an object's structure while paused: evaluate an expression and recurse into its child "
        + "members (fields/properties/elements) to a depth, returning a tree of {name,type,value,children}. "
        + "Use this to inspect a complex object without guessing every member path (e.g. expand 'order' "
        + "instead of evaluating 'order.Customer.Address.City' blind). Break-mode only.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["expression"] = new JObject { ["type"] = "string", ["description"] = "Expression to expand (e.g. 'order', 'this', 'items[0]')." },
            ["depth"] = new JObject { ["type"] = "integer", ["description"] = "Levels of children to expand, 1-3 (default 2)." },
            ["frameIndex"] = new JObject { ["type"] = "integer", ["description"] = "Frame to evaluate in, 0 = current (default 0)." },
        },
        ["required"] = new JArray("expression"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string expression = (string?)args["expression"] ?? "";
        int depth = (int?)args["depth"] ?? 2;
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.Expand(expression, frameIndex, depth);
        int kids = (result["children"] as JArray)?.Count ?? 0;
        Log.Info($"vs_expand('{expression}', depth={depth}) -> {(string?)result["mode"]}, {kids} child(ren)");
        return result;
    }
}

/// <summary>vs_threads - all threads + their stacks (deadlock/race investigation).</summary>
internal sealed class VsThreadsTool : IIdeTool
{
    public string Name => "vs_threads";
    public string Description =>
        "List ALL threads of the debugged process while paused, each with its call stack (function names), "
        + "suspended state, and location. Use for deadlocks and races. Returns {mode, threads:[...]}. "
        + "(EnvDTE gives per-thread stacks but not lock/wait-chain ownership.) Break-mode only.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadThreads();
        int n = (result["threads"] as JArray)?.Count ?? 0;
        Log.Info($"vs_threads -> mode={(string?)result["mode"]}, {n} thread(s)");
        return result;
    }
}
