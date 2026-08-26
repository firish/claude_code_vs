using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Building;

/// <summary>
/// Drives Visual Studio's own build and reports the result, so the compile loop closes the same way
/// <see cref="Testing.TestRunner"/> closed the test loop: change code -> build -> read real errors -> fix.
///
/// Why not just let the CLI shell out to <c>dotnet build</c>: for .NET Framework projects, C++ projects,
/// solution-level configurations, custom targets and the Directory.Build.props chain, the SDK build and
/// the IDE build genuinely diverge - and more importantly the Error List that <c>getDiagnostics</c> reads
/// is populated by the IDE build alone. Without this, Claude reads whatever diagnostics were left over
/// from the last time a human pressed Ctrl+Shift+B.
///
/// Two deliberate choices:
/// * <b>The build is asynchronous.</b> <c>Build(false)</c> plus polling <c>BuildState</c> keeps the UI
///   thread free; the blocking <c>Build(true)</c> that <see cref="Testing.TestRunner"/> uses would freeze
///   VS for the whole build, which is tolerable as a step inside a test run and not as a tool of its own.
/// * <b>Diagnostics come from the Error List, not from parsing the build log.</b> MSBuild's output text is
///   localized ("error"/"warning" are translated in a localized VS), so a regex over it would work in
///   English and nowhere else. The Error List hands back an enum. The raw log still ships in
///   <c>output</c>, because MSBuild-level failures (restore, a missing SDK or target) do not always
///   produce Error List rows.
/// </summary>
internal static class SolutionBuilder
{
    private const int MaxErrors = 50;
    private const int MaxWarnings = 25;
    private const int MaxOutputLines = 120;

    /// <summary>Wall-clock budget the caller gets to wait inline; the shim's HTTP timeout is 60s.</summary>
    public const int DefaultTimeoutSeconds = 45;

    public static async Task<JObject> BuildAsync(
        string? projectArg, bool rebuild, int timeoutSeconds, bool includeWarnings, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE;
        var solution = dte?.Solution;
        var sb = solution?.SolutionBuild;
        if (sb == null || solution?.IsOpen != true)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = "no solution or project is open in Visual Studio (a folder opened without a "
                          + "solution has nothing for the IDE to build)",
            };
        }

        // Optional project scope. Accepts a project name, a unique name, or the path of any file inside it -
        // "build the project that owns the file I just edited" is the common case and shouldn't need a lookup.
        string? uniqueName = null, projectName = null;
        if (!string.IsNullOrWhiteSpace(projectArg))
        {
            (uniqueName, projectName) = ResolveProject(solution, projectArg!.Trim());
            if (uniqueName == null)
            {
                return new JObject
                {
                    ["ok"] = false,
                    ["error"] = $"no project in the solution matching '{projectArg}'",
                    ["projects"] = new JArray(ListProjectNames(solution)),
                };
            }
        }

        // No active solution configuration means there is nothing for the IDE build to target - almost
        // always Open Folder mode. Catch it here: EnvDTE's own failure is the opaque
        // "Value cannot be null. Parameter name: pSlnCfg" from deep inside Build().
        string config = "";
        try { config = sb.ActiveConfiguration?.Name ?? ""; } catch { }
        if (string.IsNullOrEmpty(config))
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = "Visual Studio has no active build configuration, so there is nothing for the "
                          + "IDE build to target. This is normally a folder opened without a solution "
                          + "(Open Folder mode); it can also mean a solution that is still loading.",
                ["hint"] = "Open the .sln or .slnx in Visual Studio and retry, or build from the command "
                         + "line with `dotnet build` (which does not need the IDE).",
            };
        }

        // We CLEAR the Build pane before starting, so everything in it afterwards is this build's log and
        // nothing else. Reading a delta from a bookmark cannot be made to work here, which cost two
        // attempts to learn: a line count alone can't tell a clear from an append (VS wipes this pane at
        // the start of every build, which only looks like a shrink when the new log is shorter), and
        // bookmarking the last line's TEXT fails too, because EndPoint.Line is the empty line after the
        // final CRLF - so the bookmark is always "" and matches "" again after the next clear. Two builds
        // of the same length then read as an append and the log comes back empty. Bookmarking the last
        // NON-empty line has the same hole for two identical builds. Clearing is deterministic, and VS was
        // going to clear the pane a moment later anyway.
        var pane = OutputWindowReader.Find("build");

        bool attached = false;
        try { attached = sb.BuildState == vsBuildState.vsBuildStateInProgress; } catch { }

        if (!attached)
        {
            // Only when WE start the build. Attaching to one already running must not wipe its log -
            // and it doesn't need to, since that build cleared the pane when it started.
            try { pane?.Clear(); } catch { /* pane may be mid-teardown; reading from the top still works */ }

            try
            {
                // Rebuild = clean then build. Clean is solution-scope in EnvDTE (there is no CleanProject),
                // and it is quick, so the blocking form is fine here; the build itself stays asynchronous.
                if (rebuild && uniqueName == null) sb.Clean(true);

                if (uniqueName == null) sb.Build(false);
                else sb.BuildProject(config, uniqueName, false);
            }
            catch (Exception e)
            {
                return new JObject { ["ok"] = false, ["error"] = $"could not start the build: {e.Message}" };
            }
        }

        await TaskScheduler.Default;
        bool finished = await WaitForBuildAsync(sb, TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)), ct);

        // The Error List is flushed slightly after the build reports done; give it a moment to settle.
        if (finished) await Task.Delay(400, ct);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        int projectsFailed = -1;
        try { projectsFailed = sb.LastBuildInfo; } catch { }

        var result = new JObject
        {
            ["scope"] = projectName ?? "(whole solution)",
            ["configuration"] = config,
        };
        if (rebuild && uniqueName == null) result["rebuild"] = true;
        if (rebuild && uniqueName != null)
            result["rebuildIgnored"] = "rebuild is solution-scope (EnvDTE has no per-project clean), so this project was built incrementally";
        if (attached) result["attachedToRunningBuild"] = true;

        // Raw log tail: the language-neutral fallback for failures the Error List never sees.
        pane ??= OutputWindowReader.Find("build");
        if (pane != null)
        {
            string log = OutputWindowReader.ReadText(pane, 1, MaxOutputLines, out bool logTrunc, out _);
            if (!string.IsNullOrWhiteSpace(log))
            {
                result["output"] = log;
                if (logTrunc) result["outputTruncated"] = true;
            }
        }

        if (!finished)
        {
            result["ok"] = false;
            result["stillBuilding"] = true;
            result["note"] = $"the build was still running after {timeoutSeconds}s and continues in the "
                           + "background - call vs_build again to keep waiting for the same build (it will "
                           + "attach rather than start a second one)";
            return result;
        }

        result["ok"] = projectsFailed == 0;
        result["projectsFailed"] = projectsFailed;

        AddDiagnostics(result, includeWarnings, projectName);

        if (projectsFailed > 0 && (int?)result["errorCount"] == 0)
        {
            result["note"] = "the build failed but the Error List has no error rows, so the failure is at the "
                           + "MSBuild/project level (package restore, a missing SDK or target, a custom "
                           + "target, a pre/post-build step) - read 'output' for the raw build log";
        }

        return result;
    }

    /// <summary>Pull the current Error List into the report, capped and split by severity. UI thread.</summary>
    private static void AddDiagnostics(JObject result, bool includeWarnings, string? scopedProject)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var entries = ErrorListReader.ReadEntries();

        // Scoped build -> scoped diagnostics. Rows with no project attribution stay in: those are the
        // MSBuild-level ones, and dropping them would hide the very failure the caller is chasing.
        if (!string.IsNullOrEmpty(scopedProject))
        {
            entries = entries
                .Where(e => string.IsNullOrEmpty(e.Project) ||
                            string.Equals(e.Project, scopedProject, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var errors = entries.Where(e => e.Severity == 1).ToList();
        var warnings = entries.Where(e => e.Severity == 2).ToList();

        result["errorCount"] = errors.Count;
        result["warningCount"] = warnings.Count;

        bool truncated = false;
        result["errors"] = ToJson(errors, MaxErrors, ref truncated);
        if (includeWarnings) result["warnings"] = ToJson(warnings, MaxWarnings, ref truncated);
        if (truncated) result["truncated"] = true;

        if (errors.Count > 0 || warnings.Count > 0)
        {
            result["diagnosticsNote"] = "from Visual Studio's Error List, which merges build output with live "
                                      + "IntelliSense analysis - an entry can therefore refer to a file that "
                                      + "was not part of this build";
        }
    }

    private static JArray ToJson(List<ErrorEntry> entries, int cap, ref bool truncated)
    {
        var arr = new JArray();
        if (entries.Count > cap) truncated = true;
        foreach (var e in entries.Take(cap))
        {
            var o = new JObject { ["message"] = e.Message };
            if (!string.IsNullOrEmpty(e.File))
            {
                o["file"] = e.File;
                o["line"] = e.Line + 1;      // Error List reports 0-based; humans and the editor are 1-based
                o["column"] = e.Column + 1;
            }
            if (!string.IsNullOrEmpty(e.Project)) o["project"] = e.Project;
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>
    /// Poll BuildState until the build finishes or the budget runs out, hopping to the UI thread only for the
    /// state read. The grace window covers the gap between issuing Build(false) and the build manager
    /// actually flipping to InProgress - without it an instant "Done" left over from the PREVIOUS build
    /// would look like this one finishing immediately.
    /// </summary>
    private static async Task<bool> WaitForBuildAsync(SolutionBuild sb, TimeSpan budget, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var grace = TimeSpan.FromMilliseconds(1200);
        bool sawInProgress = false;

        while (sw.Elapsed < budget)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            vsBuildState state;
            try { state = sb.BuildState; }
            catch { state = vsBuildState.vsBuildStateDone; }
            await TaskScheduler.Default;

            if (state == vsBuildState.vsBuildStateInProgress) sawInProgress = true;
            else if (sawInProgress || sw.Elapsed > grace) return true;

            // Tight polling only while we're waiting for the build to get going; back off once it has, so a
            // long build isn't marshalling onto the UI thread seven times a second for no reason.
            await Task.Delay(sawInProgress ? 400 : 150, ct);
        }
        return false;
    }

    // ---------------- project resolution ----------------

    /// <summary>Match a project by unique name, name, or a file path inside it. UI thread.</summary>
    private static (string? uniqueName, string? name) ResolveProject(Solution solution, string arg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A path: let VS tell us which project owns the file.
        if (arg.IndexOf('\\') >= 0 || arg.IndexOf('/') >= 0)
        {
            try
            {
                var item = solution.FindProjectItem(arg.Replace('/', '\\'));
                var owner = item?.ContainingProject;
                if (owner != null) return (owner.UniqueName, owner.Name);
            }
            catch { }
        }

        foreach (var p in AllProjects(solution))
        {
            try
            {
                if (string.Equals(p.UniqueName, arg, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase))
                    return (p.UniqueName, p.Name);
            }
            catch { }
        }

        // Last resort: a substring, so "Checkout" finds "CheckoutBuggy".
        foreach (var p in AllProjects(solution))
        {
            try
            {
                if (p.Name?.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                    return (p.UniqueName, p.Name);
            }
            catch { }
        }

        return (null, null);
    }

    private static List<string> ListProjectNames(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var names = new List<string>();
        foreach (var p in AllProjects(solution))
            try { if (!string.IsNullOrEmpty(p.Name)) names.Add(p.Name); } catch { }
        return names;
    }

    /// <summary>Flatten the solution tree, descending through solution folders. UI thread.</summary>
    private static IEnumerable<Project> AllProjects(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var stack = new Stack<Project>();
        try
        {
            foreach (Project p in solution.Projects) stack.Push(p);
        }
        catch { yield break; }

        while (stack.Count > 0)
        {
            var project = stack.Pop();
            List<Project>? children = null;
            bool isFolder = false;
            try
            {
                // A solution folder holds projects as ProjectItems with a SubProject.
                isFolder = project.Kind == EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder;
                if (isFolder && project.ProjectItems != null)
                {
                    children = new List<Project>();
                    foreach (ProjectItem item in project.ProjectItems)
                        if (item.SubProject is Project sub) children.Add(sub);
                }
            }
            catch { }

            if (children != null)
                foreach (var c in children) stack.Push(c);

            if (!isFolder) yield return project;
        }
    }
}
