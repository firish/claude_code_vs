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

        // Reflect CLI connect/disconnect in the dockable panel.
        _server.ConnectionChanged += connected => Ui.BridgeStatus.SetConnected(connected);

        // Single-gate: the PreToolUse hook POSTs to /permission, which routes here to show the diff.
        _server.PermissionHandler = ShowPermissionDiffAsync;

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
    /// Single-gate permission path: show the proposed change as a REVIEW-ONLY diff (no write-back — the
    /// CLI writes the file itself once the edit is allowed) and return whether the user accepted. The
    /// bridge's /permission endpoint calls this; the PreToolUse hook posts to that endpoint.
    /// </summary>
    private async Task<bool> ShowPermissionDiffAsync(string filePath, string newContents, CancellationToken ct)
    {
        var tab = "perm:" + Guid.NewGuid().ToString("N");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claudeperm_{Guid.NewGuid():N}.tmp");
        try { System.IO.File.WriteAllText(temp, newContents); }
        catch (Exception e) { Log.Warn($"permission temp stage failed: {e.Message}"); }

        var decision = _decisions.AwaitDecisionAsync(tab);
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
        return await decision;
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
    /// T1 — launch the CLI in a terminal pre-wired to this bridge: a new console with
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

        string? workspace = await GetWorkspaceRootAsync();

        // Auto-install the single-gate PreToolUse hook into the workspace so accepting/rejecting our
        // diff is the sole edit gate (no terminal prompt). Best-effort; idempotent; safe to re-run.
        if (!string.IsNullOrEmpty(workspace))
            Hooks.PermissionHookInstaller.EnsureInstalled(workspace!);

        // Launch in DEFAULT permission mode. We tried --permission-mode acceptEdits to drop the CLI's
        // terminal edit-prompt, but verified it makes the CLI auto-apply edits and NOT call openDiff at
        // all — i.e. it kills our diff (the whole point). In the interactive-terminal model the diff and
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
        _lockfile?.Delete();
        _cts.Dispose();
    }
}
