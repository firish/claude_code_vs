using System;
using System.Collections.Generic;
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

    private VsOutputLog? _log;
    private Lockfile? _lockfile;
    private IdeWebSocketServer? _server;
    private WorkspaceWatcher? _watcher;

    public BridgeHost(AsyncPackage package) => _package = package;

    public async Task StartAsync(CancellationToken ct)
    {
        // 1) Logging first, so everything below is visible in the "Claude Code" output pane.
        _log = await VsOutputLog.CreateAsync(AsyncServiceProvider.GlobalProvider);
        _log.Install();
        Log.Info("Claude Code bridge starting…");

        // 2) Lockfile lifecycle: reap stale dead-PID files, then claim a free port. (build-plan §3)
        Lockfile.ReapStale();
        var folders = await GetWorkspaceFoldersAsync();
        _lockfile = Lockfile.CreateForFreePort(folders);

        // 3) Tool registry. Core 4 (stubbed for now) + parity stubs; the diff coordinator is shared
        //    between openDiff and the (future) Accept/Reject InfoBar.
        var decisions = new DiffDecisions();
        var tools = new ToolRegistry(BuildTools(decisions));
        var mcp = new McpServer(tools);

        // 4) Start the localhost WS server on the claimed port.
        _server = new IdeWebSocketServer(_lockfile.Port, _lockfile.AuthToken, mcp);

        // Let the selection tracker push selection_changed over this server.
        Editor.SelectionService.Attach(_server, ThreadHelper.JoinableTaskFactory);

        _ = Task.Run(() => _server.RunAsync(_cts.Token), _cts.Token);

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

    private static IEnumerable<IIdeTool> BuildTools(DiffDecisions decisions)
    {
        yield return new OpenFileTool();
        yield return new OpenDiffTool(decisions);
        yield return new GetCurrentSelectionTool();
        yield return new GetLatestSelectionTool();
        yield return new GetDiagnosticsTool();
        foreach (var stub in ParityTools.All())
            yield return stub;
    }

    /// <summary>Best-effort workspace root for the lockfile: the open solution's directory, else none.</summary>
    private async Task<IReadOnlyList<string>> GetWorkspaceFoldersAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var sol = (IVsSolution?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution));
            if (sol != null &&
                sol.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK &&
                !string.IsNullOrEmpty(dir))
            {
                return new[] { dir.TrimEnd('\\') };
            }
        }
        catch (Exception e)
        {
            Log.Warn($"workspace lookup failed: {e.Message}");
        }
        return Array.Empty<string>();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* shutting down */ }
        _watcher?.Dispose();
        _lockfile?.Delete();
        _cts.Dispose();
    }
}
