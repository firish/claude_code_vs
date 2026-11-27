using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PLog = ClaudeCodeVs.Protocol.Log;
using PLogLevel = ClaudeCodeVs.Protocol.LogLevel;

namespace ClaudeCodeVs;

/// <summary>
/// Routes the protocol core's <see cref="PLog"/> output to a "Claude Code" pane in the VS Output
/// window. Created once on the UI thread. Writes happen ONLY on the UI thread; other threads queue
/// (see <see cref="WriteLine"/> for the VS 2022 startup deadlock that rule prevents).
/// </summary>
internal sealed class VsOutputLog
{
    // Stable pane GUID so we reuse the same pane across reloads.
    private static readonly Guid PaneGuid = new("b3f5c1a2-7e0d-4c8a-9d3b-2a1f6e4c9b70");

    private readonly IVsOutputWindowPane _pane;

    private VsOutputLog(IVsOutputWindowPane pane) => _pane = pane;

    public static async Task<VsOutputLog> CreateAsync(IAsyncServiceProvider services)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var ow = await services.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (ow is null)
            throw new InvalidOperationException("SVsOutputWindow service is unavailable");

        var guid = PaneGuid;
        ow.CreatePane(ref guid, "Claude Code", fInitVisible: 1, fClearWithSolution: 0);
        ow.GetPane(ref guid, out var pane);
        return new VsOutputLog(pane);
    }

    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushScheduled;

    /// <summary>
    /// Never blocks the calling thread. Off the UI thread the line is queued and written by a UI-thread
    /// flush. "OutputStringThreadSafe" is only thread-safe once you HAVE an interface pointer for your
    /// thread: on VS 2022 the pane is an STA object, so the very first call from a pool thread has to
    /// marshal through the UI thread (GetCOMIPFromRCW) - and while VS restores a docked Claude Code panel
    /// at startup, the UI thread sits in a no-pump synchronous wait for this package to load. Background
    /// logging from BridgeHost.StartAsync then deadlocked VS on launch (seen live on 17.10; VS 2026's pane
    /// happens not to need the marshal).
    /// </summary>
    public void WriteLine(PLogLevel level, string message)
    {
        var line = $"[{level.ToString().ToLowerInvariant()}] {message}{Environment.NewLine}";
        if (ThreadHelper.CheckAccess())
        {
            Flush(); // keep order: anything queued from other threads goes first
            Write(line);
            return;
        }

        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Interlocked.Exchange(ref _flushScheduled, 0); // before draining, so a racing enqueue reschedules
                Flush();
            });
        }
    }

    private void Flush()
    {
        while (_pending.TryDequeue(out var queued)) Write(queued);
    }

    private void Write(string line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _pane.OutputStringThreadSafe(line); }
        catch { /* the log must never take the caller down */ }
    }

    /// <summary>Bring this pane forward in the Output window. Must be called on the UI thread.</summary>
    public void Activate()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _pane.Activate();
    }
}
