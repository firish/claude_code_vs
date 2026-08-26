using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Building;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// The compile half of the fix-verify loop, on the vs-debug pull surface: <c>vs_build</c> drives Visual
/// Studio's own build and hands back real diagnostics, <c>vs_read_output</c> reads the Output window panes
/// the CLI cannot see from a terminal. Both are reads-and-builds, not execution of the user's app, so
/// neither is gated - same reasoning as <c>vs_run_test</c>, and the CLI could always shell out to
/// <c>dotnet build</c> on its own anyway.
/// </summary>
internal sealed class VsBuildTool : IIdeTool
{
    public string Name => "vs_build";

    public string Description =>
        "Build the solution open in Visual Studio (or one project) with the IDE's own build, and get back "
        + "structured errors and warnings: [{file, line, column, message, project}] plus the raw build log "
        + "tail. Use this instead of shelling out to `dotnet build` or MSBuild: it is the build the IDE "
        + "actually ran, it covers .NET Framework and C++ projects and the solution's active configuration, "
        + "and it is what populates the Error List that getDiagnostics reads - so without it those "
        + "diagnostics are whatever was left over from the last manual build. Pass project = a project name "
        + "or the path of any file in it to build just that project. The build runs asynchronously (the IDE "
        + "stays responsive); if it outlasts the timeout, call vs_build again to keep waiting for the SAME "
        + "build rather than starting a second one.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["project"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Build only this project. Accepts a project name (or a substring of one) "
                                + "or the path of any file inside it. Omit to build the whole solution.",
            },
            ["rebuild"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Clean first, then build - for when an incremental build is lying about "
                                + "stale output (default false). Solution-scope only.",
            },
            ["includeWarnings"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Include warnings alongside errors (default true; the count is always reported).",
            },
            ["timeoutSeconds"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "How long to wait inline before returning with stillBuilding:true "
                                + $"(default {SolutionBuilder.DefaultTimeoutSeconds}, max 55 - the MCP transport times out at 60s).",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? project = (string?)args["project"];
        bool rebuild = (bool?)args["rebuild"] ?? false;
        bool includeWarnings = (bool?)args["includeWarnings"] ?? true;
        int timeout = Math.Min(55, Math.Max(5, (int?)args["timeoutSeconds"] ?? SolutionBuilder.DefaultTimeoutSeconds));

        JObject result;
        try
        {
            result = await SolutionBuilder.BuildAsync(project, rebuild, timeout, includeWarnings, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"vs_build failed: {e.Message}");
            return new JObject { ["ok"] = false, ["error"] = e.Message };
        }

        string outcome = (bool?)result["stillBuilding"] == true
            ? "still building"
            : ((bool?)result["ok"] == true ? "succeeded" : "FAILED");
        Log.Info($"vs_build(project={project ?? "*"}{(rebuild ? ", rebuild" : "")}) -> {outcome}"
               + $", {(int?)result["errorCount"] ?? 0} error(s), {(int?)result["warningCount"] ?? 0} warning(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>
/// vs_read_output - read a Visual Studio Output window pane. The Debug pane in particular is invisible to
/// the CLI: under F5 the debuggee's Console/ILogger output, first-chance exception notices and binding
/// failures go there rather than to any terminal.
/// </summary>
internal sealed class VsReadOutputTool : IIdeTool
{
    private const int DefaultTail = 200;
    private const int MaxTail = 5000;
    private const int DefaultMaxChars = 20000;

    public string Name => "vs_read_output";

    public string Description =>
        "Read a pane of Visual Studio's Output window - text the CLI has no other way to see. pane 'debug' "
        + "(the debugged app's own Console/ILogger output, first-chance exception notices, assembly-binding "
        + "failures, Hot Reload messages - none of which reach a terminal under F5), 'build' (the full "
        + "MSBuild log), 'general', or any other pane by name ('Tests', 'Claude Code' for this extension's "
        + "own diagnostics). Returns the LAST 'tail' lines, so a long-running log costs a bounded number of "
        + "tokens. Pass contains to keep only matching lines within that window.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pane"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "'build' (default), 'debug', 'general', or any pane's display name. "
                                + "The three aliases are matched by pane id, so they work in a non-English VS.",
            },
            ["tail"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = $"How many lines from the end to return (default {DefaultTail}, max {MaxTail}).",
            },
            ["contains"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Keep only lines containing this text (case-insensitive), applied within "
                                + "the tail window - raise 'tail' to search further back.",
            },
            ["maxChars"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = $"Hard character cap on the returned text, newest kept (default {DefaultMaxChars}).",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string paneArg = ((string?)args["pane"])?.Trim() is { Length: > 0 } p ? p : "build";
        int tail = Math.Min(MaxTail, Math.Max(1, (int?)args["tail"] ?? DefaultTail));
        string? contains = (string?)args["contains"];
        int maxChars = Math.Min(200_000, Math.Max(500, (int?)args["maxChars"] ?? DefaultMaxChars));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var pane = OutputWindowReader.Find(paneArg);
        if (pane == null)
        {
            var available = OutputWindowReader.ListPaneNames();
            Log.Info($"vs_read_output(pane={paneArg}) -> no such pane");
            return new JObject
            {
                ["error"] = $"no Output window pane matching '{paneArg}'",
                ["hint"] = "Visual Studio creates a pane lazily - the Build pane does not exist until the "
                         + "first build, and the Debug pane not until a debug session starts.",
                ["availablePanes"] = new JArray(available.Cast<object>().ToArray()),
            };
        }

        string paneName;
        try { paneName = pane.Name ?? paneArg; } catch { paneName = paneArg; }

        string text = OutputWindowReader.ReadText(pane, 1, tail, out bool truncated, out int totalLines);

        var result = new JObject { ["pane"] = paneName, ["totalLines"] = totalLines };

        if (!string.IsNullOrEmpty(contains))
        {
            var kept = text.Split('\n')
                           .Where(l => l.IndexOf(contains!, StringComparison.OrdinalIgnoreCase) >= 0)
                           .ToList();
            result["filter"] = contains;
            result["matchedLines"] = kept.Count;
            text = string.Join("\n", kept);
        }

        if (text.Length > maxChars)
        {
            text = text.Substring(text.Length - maxChars); // keep the newest end
            truncated = true;
        }

        result["text"] = text;
        result["returnedLines"] = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
        if (truncated) result["truncated"] = true;

        Log.Info($"vs_read_output(pane={paneName}, tail={tail}{(contains != null ? $", contains='{contains}'" : "")}) "
               + $"-> {(int?)result["returnedLines"]} line(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}
