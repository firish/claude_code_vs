using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Debugging;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Phase 3 DRIVE tools - execution control + breakpoint mutation, on the same /mcp pull surface as the
/// Phase 2 readers. Every one is gated behind <see cref="Ui.BridgeStatus.AllowDebuggerDrive"/> (the
/// panel's "Allow Claude to drive debugger" toggle, default OFF): when disabled the tool returns a clear
/// refusal instead of acting, so model-controlled execution is strictly opt-in. The actual EnvDTE work
/// (and the await-next-break coordination) lives in <see cref="DebuggerDriver"/>.
/// </summary>
internal abstract class DriveToolBase : IIdeTool
{
    protected readonly DebuggerDriver Driver;
    protected DriveToolBase(DebuggerDriver driver) => Driver = driver;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JToken Schema { get; }

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        if (!Ui.BridgeStatus.AllowDebuggerDrive)
        {
            Log.Info($"{Name} -> refused (driving disabled)");
            return new JObject
            {
                ["error"] = "debugger driving is disabled",
                ["hint"] = "Enable 'Allow Claude to drive debugger' in the Claude Code panel in Visual Studio, then retry.",
            };
        }

        var result = await RunAsync(args, ct);
        Log.Info($"{Name} -> {Summarize(result)}");
        return result;
    }

    protected abstract Task<JObject> RunAsync(JToken args, CancellationToken ct);

    // Make the Output line tell the story: where a continue/step landed, a timeout note, ok, or an error.
    private static string Summarize(JObject r)
    {
        if (r["error"] != null) return "error: " + (string?)r["error"];
        var mode = (string?)r["mode"];
        if (mode == "break")
        {
            var fn = (string?)r["stoppedAt"]?["function"];
            var line = r["stoppedAt"]?["line"];
            return fn != null ? $"break @ {fn}:{line}" : "break";
        }
        if (mode != null)
            return r["note"] != null ? $"{mode} ({(string?)r["note"]})" : mode;
        return (bool?)r["ok"] == true ? "ok" : "?";
    }

    // ---- schema helpers ----
    protected static JToken NoArgs() => new JObject { ["type"] = "object", ["properties"] = new JObject() };
    protected static JObject Prop(string type, string description) => new JObject { ["type"] = type, ["description"] = description };
}

internal sealed class VsContinueTool : DriveToolBase
{
    public VsContinueTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_continue";
    public override string Description =>
        "Resume execution (continue / F5) until the next breakpoint or program end, then return the new "
        + "debugger state. Requires the debugger paused and 'Allow Claude to drive' enabled.";
    public override JToken Schema => NoArgs();
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct) => Driver.ContinueAsync(DebuggerDriver.DefaultTimeoutMs, ct);
}

internal sealed class VsStepOverTool : DriveToolBase
{
    public VsStepOverTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_step_over";
    public override string Description => "Step over the current line (run called methods without stepping into them), then return the new debugger state. Paused + driving enabled.";
    public override JToken Schema => NoArgs();
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct) => Driver.StepOverAsync(DebuggerDriver.DefaultTimeoutMs, ct);
}

internal sealed class VsStepIntoTool : DriveToolBase
{
    public VsStepIntoTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_step_into";
    public override string Description => "Step into the method called on the current line, then return the new debugger state. Paused + driving enabled.";
    public override JToken Schema => NoArgs();
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct) => Driver.StepIntoAsync(DebuggerDriver.DefaultTimeoutMs, ct);
}

internal sealed class VsStepOutTool : DriveToolBase
{
    public VsStepOutTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_step_out";
    public override string Description => "Step out of the current method (run until it returns to its caller), then return the new debugger state. Paused + driving enabled.";
    public override JToken Schema => NoArgs();
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct) => Driver.StepOutAsync(DebuggerDriver.DefaultTimeoutMs, ct);
}

internal sealed class VsRunToLineTool : DriveToolBase
{
    public VsRunToLineTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_run_to_line";
    public override string Description =>
        "Continue execution until a given file:line is reached (or another breakpoint / program end "
        + "intervenes), then return the new debugger state. Implemented as a temporary breakpoint. "
        + "Paused + driving enabled.";
    public override JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["file"] = Prop("string", "Absolute path of the file to run to."),
            ["line"] = Prop("integer", "1-based line number to run to."),
        },
        ["required"] = new JArray("file", "line"),
    };
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct)
        => Driver.RunToLineAsync((string?)a["file"] ?? "", (int?)a["line"] ?? 0, DebuggerDriver.DefaultTimeoutMs, ct);
}

internal sealed class VsSetBreakpointTool : DriveToolBase
{
    public VsSetBreakpointTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_set_breakpoint";
    public override string Description =>
        "Set a breakpoint at a file:line, optionally with a condition (e.g. \"i == 5\"). Works in any mode "
        + "(you don't have to be paused). Driving enabled. Use vs_continue/vs_run_to_line to reach it.";
    public override JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["file"] = Prop("string", "Absolute path of the file."),
            ["line"] = Prop("integer", "1-based line number for the breakpoint."),
            ["condition"] = Prop("string", "Optional break-when-true condition in the debugged language."),
        },
        ["required"] = new JArray("file", "line"),
    };
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct)
        => Driver.SetBreakpointAsync((string?)a["file"] ?? "", (int?)a["line"] ?? 0, (string?)a["condition"], ct);
}

internal sealed class VsRemoveBreakpointTool : DriveToolBase
{
    public VsRemoveBreakpointTool(DebuggerDriver d) : base(d) { }
    public override string Name => "vs_remove_breakpoint";
    public override string Description => "Remove the breakpoint(s) at a given file:line. Driving enabled.";
    public override JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["file"] = Prop("string", "Absolute path of the file."),
            ["line"] = Prop("integer", "1-based line number of the breakpoint to remove."),
        },
        ["required"] = new JArray("file", "line"),
    };
    protected override Task<JObject> RunAsync(JToken a, CancellationToken ct)
        => Driver.RemoveBreakpointAsync((string?)a["file"] ?? "", (int?)a["line"] ?? 0, ct);
}
