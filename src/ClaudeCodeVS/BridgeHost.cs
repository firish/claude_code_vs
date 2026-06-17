using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using ClaudeCodeVs.Tools;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// Owns the bridge's runtime: the output-pane logger, the lockfile, the tool registry, and the
/// WebSocket server. This is the in-proc equivalent of the spike's Program.cs wiring. The WS receive
/// loop runs on a background task; tool handlers marshal to the UI thread themselves where needed.
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    private readonly AsyncPackage _package;
    private readonly CancellationTokenSource _cts = new();
    private readonly DiffDecisions _decisions = new(); // shared by openDiff and the permission gate

    private VsOutputLog? _log;
    private Lockfile? _lockfile;
    private IdeWebSocketServer? _server;
    private WorkspaceWatcher? _watcher;
    private Debugging.DebuggerDriver? _driver; // Phase 3: drives the debugger (continue/step/breakpoints)

    public BridgeHost(AsyncPackage package) => _package = package;

    /// <summary>The port the bridge is listening on, or null if not started yet.</summary>
    public int? Port => _lockfile?.Port;

    public async Task StartAsync(CancellationToken ct)
    {
        // 1) Logging first, so everything below is visible. Fan out to BOTH the VS output pane and the
        //    dockable panel's status buffer.
        _log = await VsOutputLog.CreateAsync(AsyncServiceProvider.GlobalProvider);
        var pane = _log;
        Log.Sink = (level, msg) => { pane.WriteLine(level, msg); Ui.BridgeStatus.Append(level, msg); };
        Ui.BridgeStatus.LaunchAction = LaunchClaudeAsync;
        Ui.BridgeStatus.ShowOutputAction = () => pane.Activate(); // panel's "Output" button (UI thread)
        Log.Info("Claude Code bridge starting…");

        // 2) Lockfile lifecycle: reap stale dead-PID files, then claim a free port. (build-plan §3)
        Lockfile.ReapStale();
        var folders = await GetWorkspaceFoldersAsync();
        _lockfile = Lockfile.CreateForFreePort(folders);
        Ui.BridgeStatus.SetEndpoint(_lockfile.Port, folders.Count > 0 ? folders[0] : null);

        // 3) Tool registry. The diff coordinator (_decisions) is shared between openDiff and the
        //    single-gate permission path.
        var tools = new ToolRegistry(BuildTools(_decisions));
        var mcp = new McpServer(tools);

        // 4) Start the localhost WS server on the claimed port.
        _server = new IdeWebSocketServer(_lockfile.Port, _lockfile.AuthToken, mcp);

        // Let the selection tracker push selection_changed over this server.
        Editor.SelectionService.Attach(_server, ThreadHelper.JoinableTaskFactory);

        // Reflect CLI connect/disconnect in the dockable panel. On a FULL disconnect (no clients left),
        // reject + close any orphaned diffs: their openDiff/permission caller is gone, so the parked
        // decision would never be delivered and the diff frame + InfoBar would linger. We deliberately
        // do NOT touch the lockfile - the server is still listening and the CLI needs the lockfile
        // (port + auth token) to reconnect.
        _server.ConnectionChanged += connected =>
        {
            Ui.BridgeStatus.SetConnected(connected);
            if (connected) return;
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await Diff.DiffRegistry.CloseAllAsync(); }
                catch (Exception e) { Log.Warn($"orphan diff cleanup on disconnect failed: {e.Message}"); }
            }).FileAndForget("claudecodevs/disconnectCleanup");
#pragma warning restore VSSDK007
        };

        // Single-gate: the PreToolUse hook POSTs to /permission, which routes here to show the diff.
        _server.PermissionHandler = ShowPermissionDiffAsync;

        // Stats: the Stop hook POSTs the transcript path to /usage; we parse it for tokens/cost.
        _server.UsageHandler = UsageTracker.UpdateFromTranscriptAsync;

        // Debug awareness: the UserPromptSubmit hook POSTs to /debug-context; we read the live VS
        // debugger (break location, call stack, locals) and hand it back to be injected into context.
        _server.DebugContextHandler = GetDebugContextAsync;

        // Debug PULL channel (Phase 2): a SECOND MCP server with its own registry of vs_* debug tools,
        // served at POST /mcp. The CLI reaches it through the stdio shim that McpInstaller registers in
        // .mcp.json - so the model can fetch live runtime state on demand mid-turn, not just at
        // prompt-submit. Distinct from the IDE-protocol MCP on the WebSocket above (whose tools stay
        // dormant); reuses the same McpServer dispatch over a different tool set. The driver (Phase 3)
        // owns the IVsDebugger event subscription + the await-next-break coordination for the drive tools.
        _driver = new Debugging.DebuggerDriver();
        _server.DebugMcp = new McpServer(new ToolRegistry(BuildDebugTools(_driver)));

        // Run the accept loop in the background. If it ever faults (not a normal shutdown), delete the
        // lockfile so we don't keep advertising a dead bridge that blocks reconnection (issue #5043).
        _ = Task.Run(async () =>
        {
            try { await _server.RunAsync(_cts.Token); }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception e)
            {
                Log.Error($"WS server stopped unexpectedly: {e.Message}");
                _lockfile?.Delete();
            }
        }, _cts.Token);

        // Keep the lockfile's workspaceFolders in sync as solutions/folders open, so /ide matches cwd.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var sol = (IVsSolution?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution));
        if (sol != null)
        {
            _watcher = new WorkspaceWatcher(sol, _lockfile);
            _watcher.Start();
        }

        Log.Info($"Bridge ready on port {_lockfile.Port}. To connect: run `claude` in your workspace, then /ide.");
    }

    /// <summary>
    /// Single-gate permission path: show the proposed change as a REVIEW-ONLY diff (no write-back - the
    /// CLI writes the file itself once the edit is allowed) and return whether the user accepted. The
    /// bridge's /permission endpoint calls this; the PreToolUse hook posts to that endpoint.
    /// </summary>
    private async Task<(bool allow, string? reason)> ShowPermissionDiffAsync(string filePath, string newContents, CancellationToken ct)
    {
        // Run-wild: when auto-accept is on, allow immediately without opening the diff.
        if (Ui.BridgeStatus.AutoAcceptEdits)
        {
            Log.Info($"auto-accept on: allowing {filePath} without review");
            Ui.BridgeStatus.RecordDecision(accepted: true);
            ScheduleReload(filePath);
            return (true, null);
        }

        var tab = "perm:" + Guid.NewGuid().ToString("N");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claudeperm_{Guid.NewGuid():N}.tmp");
        try { System.IO.File.WriteAllText(temp, newContents); }
        catch (Exception e) { Log.Warn($"permission temp stage failed: {e.Message}"); }

        var decision = _decisions.AwaitDecisionAsync(tab);
        Ui.BridgeStatus.AddPending(tab, filePath);
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            Diff.DiffSession.Open(filePath, filePath, newContents, tab, temp, _decisions, writeBack: false);
        }
        catch (Exception e)
        {
            Log.Error($"permission diff failed (allowing): {e.Message}");
            _decisions.Resolve(tab, true); // fail-open
        }
        var d = await decision;
        Ui.BridgeStatus.RemovePending(tab);
        Ui.BridgeStatus.RecordDecision(d.Accepted);
        if (d.Accepted)
            ScheduleReload(filePath);
        return (d.Accepted, d.RejectReason);
    }

    /// <summary>
    /// After an edit is allowed, the CLI writes the file itself; the open editor only notices on focus.
    /// Give the CLI a moment to write, then reload the doc (if clean) so it refreshes immediately.
    /// </summary>
    private void ScheduleReload(string filePath)
    {
        // Intentional fire-and-forget (FileAndForget reports faults to the activity log).
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(500, _cts.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_cts.Token);
                Editor.RunningDocuments.ReloadIfClean(filePath);
            }
            catch (Exception e) { Log.Warn($"post-edit reload failed: {e.Message}"); }
        }).FileAndForget("claudecodevs/reload");
#pragma warning restore VSSDK007
    }

    /// <summary>
    /// Read the current VS debugger state for the UserPromptSubmit hook to inject into Claude's context.
    /// Hops to the UI thread (EnvDTE is UI-thread bound). Returns a compact JSON snapshot; on any failure
    /// returns {"mode":"unknown"} so the hook simply injects nothing (fail-open, never blocks the turn).
    /// </summary>
    private async Task<string> GetDebugContextAsync(CancellationToken ct)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            // Fully qualified: `using System.Diagnostics` is in scope here, so a bare `Debugging` could
            // be misread - and `Debug` would collide with System.Diagnostics.Debug outright.
            var snap = ClaudeCodeVs.Debugging.DebuggerReader.ReadSnapshot();
            var mode = (string?)snap["mode"];
            if (mode == "break")
            {
                var fn = (string?)snap["stoppedAt"]?["function"] ?? "?";
                int frames = (snap["callStack"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                int args = (snap["args"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                int locals = (snap["locals"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
                Log.Info($"debug-context: break at {fn} ({frames} frame(s), {args} arg(s), {locals} local(s)) -> injecting");
            }
            else
            {
                // Not paused -> the hook injects nothing. Event level so normal (non-debug) turns stay quiet.
                Log.Event($"debug-context: mode={mode} (not paused; nothing injected)");
            }
            return snap.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception e)
        {
            Log.Warn($"debug-context read failed: {e.Message}");
            return "{\"mode\":\"unknown\"}";
        }
    }

    private static IEnumerable<IIdeTool> BuildTools(DiffDecisions decisions)
    {
        yield return new OpenFileTool();
        yield return new OpenDiffTool(decisions);
        yield return new GetCurrentSelectionTool();
        yield return new GetLatestSelectionTool();
        yield return new GetDiagnosticsTool();
        // Phase 2 awareness tools (RDT / solution backed).
        yield return new GetOpenEditorsTool();
        yield return new GetWorkspaceFoldersTool();
        yield return new CheckDocumentDirtyTool();
        yield return new SaveDocumentTool();
        // Phase 2 diff-tab lifecycle (real close).
        yield return new CloseTabTool();
        yield return new CloseAllDiffTabsTool();
        // Remaining stub (executeCode -> MCP error).
        foreach (var stub in ParityTools.All())
            yield return stub;
    }

    /// <summary>
    /// The Phase 2 debug PULL tools, served on the secondary /mcp surface (NOT the IDE WebSocket). Kept
    /// in a separate registry so they're real, callable MCP tools the CLI surfaces to the model - unlike
    /// the IDE-protocol tools above, which the CLI advertises but keeps dormant.
    /// </summary>
    private static IEnumerable<IIdeTool> BuildDebugTools(Debugging.DebuggerDriver driver)
    {
        // Phase 2 - read/pull (ungated).
        yield return new VsDebugStateTool();
        yield return new VsListBreakpointsTool();
        yield return new VsGetFrameLocalsTool();
        yield return new VsEvaluateTool();
        yield return new VsExpandTool();    // object-graph expansion
        yield return new VsThreadsTool();   // all threads + stacks
        // Phase 3 - drive (each gated behind BridgeStatus.AllowDebuggerDrive).
        yield return new VsContinueTool(driver);
        yield return new VsStepOverTool(driver);
        yield return new VsStepIntoTool(driver);
        yield return new VsStepOutTool(driver);
        yield return new VsRunToLineTool(driver);
        yield return new VsSetBreakpointTool(driver);
        yield return new VsRemoveBreakpointTool(driver);
        yield return new VsFreezeThreadTool(driver);      // freeze/thaw a thread
        yield return new VsSetNextStatementTool(driver);  // move the execution pointer
        // Phase 3 - session control (start = F5 to first break, stop = Shift+F5).
        yield return new VsStartDebuggingTool(driver);
        yield return new VsStopDebuggingTool(driver);
    }

    /// <summary>Best-effort workspace root for the lockfile: the open solution's directory, else none.</summary>
    private async Task<IReadOnlyList<string>> GetWorkspaceFoldersAsync()
    {
        var root = await GetWorkspaceRootAsync();
        return root is null ? Array.Empty<string>() : new[] { root };
    }

    /// <summary>The open solution/folder root, or null. Must be awaited on any thread (switches to UI).</summary>
    private async Task<string?> GetWorkspaceRootAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var sol = (IVsSolution?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution));
            if (sol != null &&
                sol.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK &&
                !string.IsNullOrEmpty(dir))
            {
                return dir.TrimEnd('\\');
            }
        }
        catch (Exception e)
        {
            Log.Warn($"workspace lookup failed: {e.Message}");
        }
        return null;
    }

    /// <summary>
    /// T1 - launch the CLI in a terminal pre-wired to this bridge: a new console with
    /// ENABLE_IDE_INTEGRATION + CLAUDE_CODE_SSE_PORT set and the working directory pinned to the
    /// workspace root, so the CLI auto-connects (no /ide) and writes files into the right repo (fixes B2).
    /// </summary>
    public async Task LaunchClaudeAsync()
    {
        if (_lockfile is null)
        {
            Log.Warn("Launch Claude Code: bridge isn't running yet.");
            return;
        }

        // Reap zombie lockfiles (dead/recycled-PID instances) before launching, so the CLI's /ide and
        // our hooks see only live bridges. Our own lockfile is alive, so it's never reaped.
        Lockfile.ReapStale();

        string? workspace = await GetWorkspaceRootAsync();

        // Auto-install the single-gate PreToolUse hook into the workspace so accepting/rejecting our
        // diff is the sole edit gate (no terminal prompt). Best-effort; idempotent; safe to re-run.
        // Also register the debug PULL MCP server (.mcp.json + stdio shim) for Phase 2 pull-on-demand.
        if (!string.IsNullOrEmpty(workspace))
        {
            Hooks.PermissionHookInstaller.EnsureInstalled(workspace!);
            Hooks.McpInstaller.EnsureInstalled(workspace!);
        }

        // Launch in DEFAULT permission mode. We tried --permission-mode acceptEdits to drop the CLI's
        // terminal edit-prompt, but verified it makes the CLI auto-apply edits and NOT call openDiff at
        // all - i.e. it kills our diff (the whole point). In the interactive-terminal model the diff and
        // the terminal prompt are inseparable: openDiff only fires in review-required (default) mode,
        // which is also what shows the terminal prompt. A true single-gate UX needs the subprocess +
        // --permission-prompt-tool stdio model (Phase 3b, where we own chat I/O). For now: diff works,
        // terminal prompt is a redundant second gate (known limitation).
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/K claude",                 // /K keeps the window open after claude exits
            UseShellExecute = false,                 // required to pass Environment below
            CreateNoWindow = false,                  // give it its own console window
        };
        psi.Environment["ENABLE_IDE_INTEGRATION"] = "true";
        psi.Environment["CLAUDE_CODE_SSE_PORT"] = _lockfile.Port.ToString();
        if (!string.IsNullOrEmpty(workspace))
            psi.WorkingDirectory = workspace;

        try
        {
            Process.Start(psi);
            Log.Info($"Launched Claude Code (port {_lockfile.Port}, cwd '{workspace ?? "(default)"}').");
        }
        catch (Exception e)
        {
            Log.Error($"Launch Claude Code failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* shutting down */ }
        _watcher?.Dispose();
        _driver?.Dispose(); // unadvise the IVsDebugger event sink (best-effort)
        _lockfile?.Delete();
        _cts.Dispose();
    }
}
