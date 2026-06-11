using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PLog = ClaudeCodeVs.Protocol.Log;
using PLogLevel = ClaudeCodeVs.Protocol.LogLevel;

namespace ClaudeCodeVs;

/// <summary>
/// Routes the protocol core's <see cref="PLog"/> output to a "Claude Code" pane in the VS Output
/// window. Created once on the UI thread; thereafter <see cref="WriteLine"/> uses
/// <c>OutputStringThreadSafe</c>, which is safe to call from the off-thread WS receive loop — so no
/// per-line marshaling is needed (CLAUDE.md convention #1 is about VS *editor/solution* calls; the
/// output pane has its own thread-safe entry point).
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

    public void WriteLine(PLogLevel level, string message)
    {
        var line = $"[{level.ToString().ToLowerInvariant()}] {message}{Environment.NewLine}";
        // OutputStringThreadSafe is the documented thread-safe entry point, so the off-thread WS loop
        // can call it directly. VSTHRD010 flags all IVsOutputWindowPane members as main-thread-only and
        // can't see that exception — suppress it locally.
#pragma warning disable VSTHRD010
        _pane.OutputStringThreadSafe(line);
#pragma warning restore VSTHRD010
    }

    /// <summary>Bring this pane forward in the Output window. Must be called on the UI thread.</summary>
    public void Activate()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _pane.Activate();
    }
}
