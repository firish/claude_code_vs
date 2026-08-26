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
    private Debugging.DataBreakpointBridge? _dataBpBridge; // managed data breakpoints (Concord component bridge)

    // When the last guarded (docked) launch was fired. HasConnections only flips once the CLI's WebSocket
    // lands, so between the click and that handshake the connection guard alone still lets repeat clicks
    // pile up terminals; this covers that window. Read/written on the UI thread only (both callers are
    // menu/button handlers) and before any await, so no synchronization is needed.
    // notes: a cooldown, not a real "starting" signal - the CLI gives us nothing to await on. Sized to
    // VsTerminalLauncher's ~10s stall timeout; widen it if cold starts outgrow it.
    private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(10);
    private DateTime _lastLaunchUtc = DateTime.MinValue;

    // "Connected but the PULL tools didn't load" detection. The IDE WebSocket auto-connects at CLI
    // startup; if the CLI also loaded our MCP servers, the stdio shim's handshake hits /mcp within a
    // couple seconds. If nothing hits /mcp within this window after a connect, the vs-debug/vs-semantic/
    // test tools aren't available (Claude was launched outside the workspace, or the project servers
    // weren't approved) - we raise a panel banner instead of failing silently. _mcpEverSeen is sticky
    // for the bridge's life so a WS reconnect of an already-proven session never re-warns.
    // 30s, not 10: the MCP servers reach us through vs-mcp-shim.ps1, so the handshake waits on TWO cold
    // PowerShell 5.1 starts (vs-debug + vs-semantic) behind whatever the machine's AV does to script
    // launches. 10s was tight enough that an ordinary cold start could raise the banner and then have it
    // silently retracted seconds later by the late handshake - leaving a scary, no-longer-true line in
    // the feed. A session that genuinely never loaded the config never resolves, so waiting longer only
    // costs the warning some latency in the case where it is real.
    private static readonly TimeSpan McpGraceWindow = TimeSpan.FromSeconds(30);
    private readonly object _mcpGate = new();
    private CancellationTokenSource? _mcpGraceCts;
    private volatile bool _mcpEverSeen;

    // Hooks-without-IDE-channel detection (the "launched outside the extension" fingerprint).
    private volatile bool _wsEverConnected;
    private int _hooksOnlyWarned; // Interlocked once-latch (hook POSTs race on the listener threads)

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
        Ui.BridgeStatus.LaunchAction = () => LaunchClaudeAsync();
        Ui.BridgeStatus.LaunchExternalAction = () => LaunchClaudeAsync(forceExternal: true);
        Ui.BridgeStatus.RelaunchAction = () => LaunchClaudeAsync(forceRelaunch: true);
        Ui.BridgeStatus.ShowOutputAction = () => pane.Activate(); // panel's "Output" button (UI thread)
        Ui.BridgeStatus.FocusClaudeAction = () => FocusClaudeAsync(); // context actions: focus so Enter sends
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

        // Session-ownership gate for the hook endpoints (PR #28): the hooks fall back to any
        // listening bridge when no workspace matches their session's cwd, so the server compares
        // each POST's cwd against OUR workspace and ignores foreign sessions. A provider, not a
        // snapshot - BridgeStatus.Workspace follows the solution as it loads/changes.
        _server.WorkspaceProvider = () => Ui.BridgeStatus.Workspace;

        // Let the selection tracker push selection_changed over this server.
        Editor.SelectionService.Attach(_server, ThreadHelper.JoinableTaskFactory);

        // Attachment tray: the panel's drop/paste target stages files and pushes at_mentioned over
        // this server, so the reference lands in the CLI's composer (unsent items flush on connect).
        Attachments.AttachmentService.Attach(_server);

        // Reflect CLI connect/disconnect in the dockable panel. On a FULL disconnect (no clients left),
        // reject + close any orphaned diffs: their openDiff/permission caller is gone, so the parked
        // decision would never be delivered and the diff frame + InfoBar would linger. We deliberately
        // do NOT touch the lockfile - the server is still listening and the CLI needs the lockfile
        // (port + auth token) to reconnect.
        _server.ConnectionChanged += connected =>
        {
            Ui.BridgeStatus.SetConnected(connected);
            WatchMcpLoad(connected); // arm/stand-down the "PULL tools didn't load" detector
            if (connected)
            {
                _wsEverConnected = true;
                Ui.BridgeStatus.SetHooksOnlyWarning(false); // a real connection supersedes the /ide nudge
                // Install-on-connect (marketplace feedback): a session that reaches the bridge without
                // going through our Launch button (manual `claude` + /ide, a fresh clone whose committed
                // settings.json references our hooks) used to hit "-File does not exist" on every prompt
                // because the scripts were never materialized. Idempotent; runs off-thread.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ws = await GetWorkspaceRootAsync();
                        if (!string.IsNullOrEmpty(ws))
                        {
                            Hooks.PermissionHookInstaller.EnsureInstalled(ws!);
                            Hooks.McpInstaller.EnsureInstalled(ws!);
                        }
                    }
                    catch (Exception e) { Log.Warn($"install-on-connect failed: {e.Message}"); }
                });
                return;
            }
            Ui.BridgeStatus.SetCliPermissionMode(null); // the observed mode belonged to the session that just ended
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try { await Diff.DiffRegistry.CloseAllAsync(); }
                catch (Exception e) { Log.Warn($"orphan diff cleanup on disconnect failed: {e.Message}"); }
            }).FileAndForget("claudecodevs/disconnectCleanup");
#pragma warning restore VSSDK007
        };

        // The other half of the detector: any /mcp or /mcp-semantic hit proves the MCP servers loaded.
        _server.McpActivity += OnMcpActivity;

        // The THIRD detection leg (issue #17 follow-up): hook POSTs arriving while the IDE WebSocket
        // has NEVER connected = a session launched outside the extension (hooks loaded, IDE channel
        // never dialed - the panel would otherwise sit silently in its idle "Waiting for CLI" state
        // while token stats tick up). Warn once; a later connect clears it, and /ide is the fix.
        _server.HookActivity += OnHookActivity;

        // Single-gate: the PreToolUse hook POSTs to /permission, which routes here to show the diff.
        _server.PermissionHandler = ShowPermissionDiffAsync;

        // Keep the panel's run-wild checkbox honest between edits: every owned hook POST that carries a
        // permission mode refreshes it, so a shift+tab out of auto mode unlocks the toggle at the next
        // prompt or turn end instead of waiting for an edit that may never come.
        _server.PermissionModeObserved = mode => Ui.BridgeStatus.SetCliPermissionMode(mode);

        // Stats: the Stop hook POSTs the transcript path to /usage; we parse it for tokens/cost.
        _server.UsageHandler = UsageTracker.UpdateFromTranscriptAsync;

        // Notifications: the Stop hook's /usage POST doubles as the turn-end signal ("Claude finished
        // responding"), and the Notification hook POSTs /notify when Claude needs the user (a terminal
        // permission prompt, or it went idle waiting for input). Both raise a main-window InfoBar +
        // taskbar flash via the Notifier; gated by the panel's Notify toggle.
        _server.StopReceived += Ui.Notifier.TurnEnded;
        _server.NotifyHandler = (message, _) => { Ui.Notifier.NeedsAttention(message); return Task.CompletedTask; };

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
        // Managed data breakpoints: the file-IPC bridge to the bundled Concord component. Owns a
        // background tailer of the change stream; needs the driver for stop-on-change (EnvDTE Break).
        _dataBpBridge = new Debugging.DataBreakpointBridge(_driver);
        _server.DebugMcp = new McpServer(new ToolRegistry(BuildDebugTools(_driver, _dataBpBridge)));
        // vs-semantic PULL channel (POST /mcp-semantic): Roslyn code-navigation tools. Same shim/dispatch,
        // its own registry + route - the static-analysis sibling of the runtime debug tools above. Reads the
        // live VisualStudioWorkspace; needs no debug session (useful any time a C#/VB solution is loaded).
        _server.SemanticMcp = new McpServer(new ToolRegistry(BuildSemanticTools()));

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
    private async Task<(bool allow, string? reason)> ShowPermissionDiffAsync(string filePath, string newContents, string? permissionMode, CancellationToken ct)
    {
        // Surface the session's mode to the panel: the run-wild checkbox reflects (and locks to) the
        // CLI's own choice while it pre-approves edits.
        Ui.BridgeStatus.SetCliPermissionMode(string.IsNullOrEmpty(permissionMode) ? null : permissionMode);

        // Honor the CLI's own permission mode (issue #17): when the user put the session in a mode that
        // pre-approves edits - including shift+tab's "auto mode", which reports 'auto' (issue #38) - our
        // gate must not be stricter than that explicit choice. Older CLIs send no mode -> gate as always.
        if (Ui.BridgeStatus.IsPreApprovingMode(permissionMode))
        {
            Log.Info($"CLI permission mode '{permissionMode}' - allowing {filePath} without the diff");
            Ui.BridgeStatus.RecordDecision(accepted: true);
            ScheduleReload(filePath);
            return (true, null);
        }

        // An unrecognized mode gates (fail-visible: the user can still accept), but says so loudly -
        // the CLI's mode vocabulary has grown before and this is how we hear about the next one.
        if (!Ui.BridgeStatus.IsKnownMode(permissionMode))
            Log.Warn($"unrecognized CLI permission mode '{permissionMode}' - showing the diff. If this mode means "
                   + "\"don't ask\", please report it: https://github.com/firish/claude_code_vs/issues");

        // Selective gate (marketplace feedback): the CLI's own working files - its ~/.claude
        // memory/config tree, temp-dir scratch files, and the workspace's .claude/ internals - skip the
        // diff entirely, so a session scaffolding scratch code or writing memory never stalls on review.
        // PROJECT CODE stays gated: every create or edit under the workspace (outside .claude/) still
        // opens the diff.
        if (IsScratchOrMemoryPath(filePath, Ui.BridgeStatus.Workspace))
        {
            Log.Info($"scratch/memory write auto-allowed (not project code): {filePath}");
            return (true, null);
        }

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
    /// True for the CLI's own working areas, which the edit gate skips: the user-level ~/.claude tree
    /// (auto-memory, config), anything under the machine temp dir (the CLI's scratchpad lives there),
    /// and the workspace's .claude/ subtree (our scripts, attachments, CLI settings). Deliberately NOT
    /// path-pattern guessing beyond that - a "scratch/" folder inside the repo is still project code.
    /// </summary>
    private static bool IsScratchOrMemoryPath(string filePath, string? workspace)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(filePath);
            bool Under(string root) =>
                !string.IsNullOrEmpty(root) &&
                full.StartsWith(root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Under(System.IO.Path.Combine(home, ".claude"))) return true;
            if (Under(System.IO.Path.GetTempPath())) return true;
            if (!string.IsNullOrEmpty(workspace) && Under(System.IO.Path.Combine(workspace!, ".claude"))) return true;
            return false;
        }
        catch
        {
            return false; // unparseable path -> gate it (fail toward review)
        }
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
            // Bound the UI-thread hop. This hook runs on EVERY prompt; if VS's main thread is busy (a
            // build, an F5 deploy, a modal dialog) the switch blocks until it frees - long enough that the
            // CLI kills the 10s UserPromptSubmit hook and discards its output. We only inject in BREAK
            // mode, and at a breakpoint the UI thread is idle (so the switch is instant). A busy UI thread
            // means we're NOT paused, so bailing fast with "unknown" (inject nothing) loses nothing. The
            // 2s cap sits under the hook's client-side HTTP timeout, so the hook returns cleanly.
            using var switchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            switchCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(switchCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Event("debug-context: VS UI thread busy (>2s) - not paused; injecting nothing this turn");
                return "{\"mode\":\"unknown\"}";
            }
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

    /// <summary>
    /// Detect the "IDE WebSocket connected but the PULL MCP tools didn't load" gap. On connect we arm a
    /// grace window; if no /mcp activity (the shim's startup handshake) arrives before it elapses, the
    /// vs-debug/vs-semantic/test tools aren't available for this session - surface a panel banner with the
    /// remedy. <see cref="OnMcpActivity"/> is the other half. Runs on the WS accept thread; BridgeStatus
    /// marshals to the UI. The warning is set and cleared under <see cref="_mcpGate"/> so a late handshake
    /// racing the timer can't leave the banner stuck on.
    /// </summary>
    private void WatchMcpLoad(bool connected)
    {
        if (!connected)
        {
            lock (_mcpGate)
            {
                _mcpGraceCts?.Cancel();
                _mcpGraceCts?.Dispose();
                _mcpGraceCts = null;
                Ui.BridgeStatus.SetToolsWarning(false);
            }
            return;
        }

        // Already proven (this CLI, or a prior one on this bridge): a WS reconnect doesn't re-handshake
        // MCP, so don't re-arm - that would false-warn a healthy session.
        if (_mcpEverSeen) return;

        CancellationToken token;
        lock (_mcpGate)
        {
            _mcpGraceCts?.Cancel();
            _mcpGraceCts?.Dispose();
            _mcpGraceCts = new CancellationTokenSource();
            token = _mcpGraceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(McpGraceWindow, token); }
            catch (OperationCanceledException) { return; } // disconnected, or MCP activity arrived first
            lock (_mcpGate)
            {
                if (_mcpEverSeen || token.IsCancellationRequested) return;
                Ui.BridgeStatus.SetToolsWarning(true);
            }
            Log.Warn("This session never loaded the workspace's .claude configuration (edit-review diff, " +
                     "notifications, and the vs-debug / vs-semantic / test tools are all inactive) - Claude was " +
                     "likely started outside or in a subfolder of the workspace. Relaunch from the panel (pins " +
                     "the right folder) or start claude at the workspace root; approve the project MCP servers if prompted.");
        });
    }

    /// <summary>
    /// First /mcp or /mcp-semantic hit - the CLI loaded our MCP servers, so stand the warning down (even a
    /// late hit after the user approves the project servers clears it). Sticky: every subsequent MCP
    /// request short-circuits on the volatile read before taking the lock.
    /// </summary>
    /// <summary>
    /// Hook traffic with no IDE connection ever seen: a `claude` session is alive and using this
    /// workspace's hooks, but it wasn't launched from the panel and never dialed the WebSocket - so
    /// the diff/selection channel is dark while the panel looks merely idle. Surface the fix instead
    /// of staying silent. Once per bridge lifetime; superseded the moment a client connects.
    /// </summary>
    private void OnHookActivity()
    {
        if (_wsEverConnected) return;
        if (System.Threading.Interlocked.Exchange(ref _hooksOnlyWarned, 1) == 1) return;
        Ui.BridgeStatus.SetHooksOnlyWarning(true);
        Log.Warn("A Claude session is using this workspace's hooks but never connected the IDE channel " +
                 "(launched outside the extension). Run /ide in that terminal and pick Visual Studio to " +
                 "light up the diff and selection sync, or relaunch from the panel.");
    }

    private void OnMcpActivity()
    {
        if (_mcpEverSeen) return; // steady-state fast path (fires on every MCP request)
        bool retracting;
        lock (_mcpGate)
        {
            if (_mcpEverSeen) return; // another request won the race while we waited on the lock
            _mcpEverSeen = true;
            _mcpGraceCts?.Cancel();
            retracting = Ui.BridgeStatus.ToolsWarning;
            Ui.BridgeStatus.SetToolsWarning(false);
        }
        // Say so when a late handshake stands the banner down. The banner disappearing on its own is
        // fine; leaving the warning's feed line as the last word on the subject is not - the user would
        // keep reading "this session never loaded the workspace's .claude configuration" long after it
        // stopped being true (logged outside the lock: the sink fans out to the panel).
        if (retracting)
            Log.Info("vs-debug / vs-semantic connected after all - the warning above no longer applies " +
                     "(the MCP servers just took a while to start).");
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
    private static IEnumerable<IIdeTool> BuildDebugTools(Debugging.DebuggerDriver driver, Debugging.DataBreakpointBridge dataBp)
    {
        // Build + Output window - the compile half of the fix-verify loop (docs/BUILD.md). vs_build is what
        // makes getDiagnostics honest: the Error List is populated by the IDE's build, so without a tool to
        // drive it the model reads whatever the last manual Ctrl+Shift+B left behind.
        yield return new VsBuildTool();
        yield return new VsReadOutputTool();

        // Test integration - VS's Test Explorer engine as a discover -> run -> debug -> catch loop (docs/TESTING.md).
        var testRunner = new Testing.TestRunner();
        yield return new VsListTestsTool(testRunner);   // discover (Roslyn)
        yield return new VsRunTestTool(testRunner);     // run one/all + coverage
        yield return new VsRerunFailedTool(testRunner); // re-run only the last run's failures
        yield return new VsDebugTestTool(testRunner);   // launch one under the debugger
        yield return new VsHuntFlakyTool(testRunner);   // force-reproduce a flaky failure (async start+poll)
        yield return new VsHuntResultTool(testRunner);  // poll a background hunt
        yield return new VsHuntCancelTool(testRunner);  // cancel a background hunt
        yield return new VsCatchFlakyTool(testRunner, driver); // catch red-handed under the debugger
        // Phase 2 - read/pull (ungated).
        yield return new VsDebugStateTool();
        yield return new VsListBreakpointsTool();
        yield return new VsGetFrameLocalsTool();
        yield return new VsEvaluateTool();
        yield return new VsExpandTool();    // object-graph expansion
        yield return new VsThreadsTool();   // all threads + stacks
        yield return new VsExceptionTool();      // inspect $exception at a throw / in a catch
        yield return new VsListProcessesTool();  // attach targets (debug real running apps)
        yield return new VsWaitChainsTool();     // ClrMD snapshot: structured lock ownership + deadlock suspects
        yield return new VsAsyncStacksTool();    // ClrMD snapshot: logical async call-stack reconstruction
        yield return new VsHeapStatsTool();      // ClrMD snapshot: heap composition + GC/handle/finalizer health
        yield return new VsThreadPoolTool();     // ClrMD snapshot: threadpool counts + starvation
        yield return new VsGcRootsTool();        // ClrMD snapshot: retention path (why is X alive)
        yield return new VsHeapDiffTool();       // ClrMD snapshot: leak finder (baseline vs now)
        // Phase 3 - drive (each gated behind BridgeStatus.AllowDebuggerDrive).
        yield return new VsContinueTool(driver);
        yield return new VsStepOverTool(driver);
        yield return new VsStepIntoTool(driver);
        yield return new VsStepOutTool(driver);
        yield return new VsRunToLineTool(driver);
        yield return new VsBreakAllTool(driver);          // pause a running/hung debuggee (Break All)
        yield return new VsSetBreakpointTool(driver);
        yield return new VsRemoveBreakpointTool(driver);
        yield return new VsBreakOnThrownTool(driver);     // first-chance break at a managed exception's throw site
        yield return new VsFreezeThreadTool(driver);      // freeze/thaw a thread
        yield return new VsSetNextStatementTool(driver);  // move the execution pointer
        // Phase 3 - session control (start = F5 to first break, stop = Shift+F5).
        yield return new VsStartDebuggingTool(driver);
        yield return new VsStopDebuggingTool(driver);
        // Tier 1 - attach to a real running app (web / service / desktop), not just F5 launch.
        yield return new VsAttachTool(driver);
        yield return new VsDetachTool(driver);

        // Managed data breakpoints (Concord component + file-IPC bridge): watch an instance field,
        // stream every change, optionally break-on-change. vs_set_* is gated drive; vs_get_* is read.
        yield return new VsSetDataBreakpointTool(dataBp);
        yield return new VsGetDataChangesTool(dataBp);
        yield return new VsRemoveDataBreakpointTool(dataBp);

        // Screen capture (gated behind BridgeStatus.AllowScreenCapture): the model takes its own
        // screenshots - the debuggee's window, a window by title (the browser case), or the screen -
        // staged as attachment chips, path returned for a native-cost Read.
        yield return new VsCaptureWindowTool();
        yield return new VsCaptureScreenTool();
    }

    /// <summary>
    /// The vs-semantic tool set - Roslyn code navigation over the live VisualStudioWorkspace. All read-only
    /// and ungated (no execution, no mutation), managed (C#/VB) only. vs_search_symbols is the addressing
    /// primitive whose symbolId the rest consume. See RoslynReader for the workspace/threading model.
    /// </summary>
    private static IEnumerable<IIdeTool> BuildSemanticTools()
    {
        yield return new VsGetSelectionTool();         // editor selection/caret -> text + symbolId at it
        yield return new VsDecompileTool();            // framework/NuGet metadata symbol -> decompiled C#
        yield return new VsSearchSymbolsTool();        // name -> symbolId (addressing primitive)
        yield return new VsFindReferencesTool();       // semantic find-all-references
        yield return new VsGoToDefinitionTool();       // the one definition among overloads
        yield return new VsFindImplementationsTool();  // interface/abstract -> concrete + overrides
        yield return new VsCallHierarchyTool();        // transitive callers / direct callees
        yield return new VsTypeHierarchyTool();        // base chain+interfaces / derived types
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
    /// Prefers VS's native docked Terminal; <paramref name="forceExternal"/> skips it for users who want
    /// a standalone console window (which, unlike the docked tab, survives closing VS).
    /// <paramref name="forceRelaunch"/> bypasses the duplicate-terminal guard below - it's the "hooks &amp;
    /// tools didn't load" banner's Relaunch button deliberately re-pinning a misconfigured session, which
    /// is exactly the case the guard would otherwise block; it refuses instead when no workspace is open.
    /// </summary>
    public async Task LaunchClaudeAsync(bool forceExternal = false, bool forceRelaunch = false)
    {
        if (_lockfile is null)
        {
            Log.Warn("Launch Claude Code: bridge isn't running yet.");
            return;
        }

        // Guards the plain (docked) Launch button against piling up redundant terminals on repeat clicks
        // while a session is already connected. External console is exempt - it's a standalone window the
        // user explicitly asked for each time, and upstream allows unlimited concurrent external consoles.
        // The "hooks & tools didn't load" banner's Relaunch button passes forceRelaunch=true to bypass this
        // too - that flow is a deliberate re-pin of a *misconfigured* connected session, not an accidental duplicate.
        if (!forceExternal)
        {
            // The already-connected guard is for the plain Launch button only. Relaunch bypasses it by
            // design (its whole job is replacing a connected-but-misconfigured session)…
            if (!forceRelaunch && _server?.HasConnections == true)
            {
                Log.Warn("Launch Claude Code: already connected - not opening another terminal.");
                return;
            }
            // …but the cooldown applies to BOTH, because a second click during the connect window piles
            // up terminals exactly the same way whichever button produced it - and Relaunch is clicked
            // from a banner that stays up for the seconds the new session needs to connect, so it invites
            // the double-click this catches.
            if (DateTime.UtcNow - _lastLaunchUtc < LaunchCooldown)
            {
                Log.Warn("Launch Claude Code: a session is still starting - give it a few seconds.");
                return;
            }
            _lastLaunchUtc = DateTime.UtcNow;
        }

        // Reap zombie lockfiles (dead/recycled-PID instances) before launching, so the CLI's /ide and
        // our hooks see only live bridges. Our own lockfile is alive, so it's never reaped.
        Lockfile.ReapStale();

        string? workspace = await GetWorkspaceRootAsync();

        // The Relaunch button's whole promise is "pins the right folder" - if VS itself has no
        // folder/workspace open, there is no right folder to pin, so relaunching would just spawn
        // another equally-unpinned session that hits the same "hooks didn't load" warning again,
        // inviting an endless click-relaunch-fail loop. Refuse instead of piling up dead terminals.
        if (forceRelaunch && string.IsNullOrEmpty(workspace))
        {
            Log.Warn("Relaunch Claude Code: no folder/workspace open in Visual Studio - open one " +
                     "(File > Open > Folder) so the CLI has a project to pin to, then relaunch.");
            return;
        }

        // Auto-install the single-gate PreToolUse hook into the workspace so accepting/rejecting our
        // diff is the sole edit gate (no terminal prompt). Best-effort; idempotent; safe to re-run.
        // Also register the debug PULL MCP server (.mcp.json + stdio shim) for Phase 2 pull-on-demand.
        if (!string.IsNullOrEmpty(workspace))
        {
            Hooks.PermissionHookInstaller.EnsureInstalled(workspace!);
            Hooks.McpInstaller.EnsureInstalled(workspace!);
        }

        // Prefer VS's own native Terminal tool window (undocumented, no NuGet package - see
        // Terminal/VsTerminalLauncher.cs). TryLaunchAsync never throws; on ANY failure it logs via
        // Log.Warn and returns false, so the external cmd.exe console below is always the safety net.
        if (!forceExternal &&
            await Terminal.VsTerminalLauncher.TryLaunchAsync(workspace, _lockfile.Port, _cts.Token))
            return;

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
            // Run-wild at launch = start the session in acceptEdits, the CLI mode that pre-approves
            // edits, so the checkbox and the session agree from the first prompt. (A RUNNING session's
            // mode cannot be changed from outside; mid-session the checkbox still auto-allows on the
            // bridge side, and shift+tab in the terminal is the CLI-side lever.)
            Arguments = "/K claude" + (Ui.BridgeStatus.AutoAcceptEdits ? " --permission-mode acceptEdits" : ""), // /K keeps the window open after claude exits
            UseShellExecute = false,                 // required to pass Environment below
            CreateNoWindow = false,                  // give it its own console window
        };
        psi.Environment["ENABLE_IDE_INTEGRATION"] = "true";
        psi.Environment["CLAUDE_CODE_SSE_PORT"] = _lockfile.Port.ToString();
        if (!string.IsNullOrEmpty(workspace))
            psi.WorkingDirectory = workspace;

        try
        {
            _externalCli = Process.Start(psi); // kept so the context actions can refocus its window
            Log.Info($"Launched Claude Code (port {_lockfile.Port}, cwd '{workspace ?? "(default)"}').");
        }
        catch (Exception e)
        {
            Log.Error($"Launch Claude Code failed: {e.Message}");
        }
    }

    private Process? _externalCli;

    /// <summary>
    /// Bring the claude session's input to the foreground so Enter sends what a context action just
    /// pushed into the composer (the #33 focus trap, solved instead of documented). Native docked tab
    /// first (ShowAsync on the remembered guid), then the external console window we launched.
    /// Best-effort - a /ide-connected terminal we didn't launch has no handle to focus.
    /// </summary>
    public async Task FocusClaudeAsync()
    {
        if (await Terminal.VsTerminalLauncher.TryFocusAsync(_cts.Token)) return;
        try
        {
            var p = _externalCli;
            if (p is { HasExited: false } && p.MainWindowHandle != IntPtr.Zero)
                SetForegroundWindow(p.MainWindowHandle);
        }
        catch { /* focus is best-effort, never let it surface */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* shutting down */ }
        try { lock (_mcpGate) { _mcpGraceCts?.Cancel(); _mcpGraceCts?.Dispose(); _mcpGraceCts = null; } } catch { /* shutting down */ }
        _watcher?.Dispose();
        _driver?.Dispose(); // unadvise the IVsDebugger event sink (best-effort)
        _dataBpBridge?.Dispose(); // stop the data-breakpoint change tailer
        _lockfile?.Delete();
        _cts.Dispose();
    }
}
