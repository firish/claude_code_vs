using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PLog = ClaudeCodeVs.Protocol.Log;

namespace ClaudeCodeVs.Edits;

/// <summary>
/// A session-long log of Claude's edits in a "Claude Code Edits" Output window pane, navigable with F8.
/// The sibling of the panel's Edits list from the same data: the panel shows the CURRENT TURN compactly
/// and is where you CLICK; this keeps the whole session and is where you STEP.
///
/// Verified behaviour in VS 2026: the lines do NOT render as hyperlinks and double-click does nothing,
/// but F8 walks them correctly. Don't chase the hyperlink - the fix would be CAT_BUILDCOMPILE, which puts
/// these rows in the Error List and re-creates the diagnostic pollution 1.20.0 removed.
///
/// Navigable output lines come from <c>OutputTaskItemString</c>, which is the only way VS offers - it
/// associates the line with a task item, and that association is what F8 follows. The items go
/// to <see cref="VSTASKCATEGORY.CAT_MISC"/> (the Task List window), deliberately NOT to CAT_BUILDCOMPILE,
/// which would put them in the Error List and inflate the very counts we just spent 1.20.0 cleaning up.
///
/// Lines stay English and unlocalized, like the protocol log pane: they are diagnostics that need to stay
/// greppable in a bug report. The panel is the localized surface.
///
/// UI thread only - unlike OutputStringThreadSafe there is no off-thread entry point for task items.
/// </summary>
internal static class EditOutputLog
{
    private static readonly Guid PaneGuid = new("c7a41e58-3d92-4b06-8f14-5a2e9b7d0c33");

    /// <summary>Task items accumulate with the lines, so wrap rather than grow without bound.</summary>
    private const int MaxEntries = 400;

    private static IVsOutputWindowPane? _pane;
    private static int _entries;

    public static void Append(IReadOnlyList<EditEntry> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (rows is null || rows.Count == 0) return;

        var pane = EnsurePane();
        if (pane is null) return;

        if (_entries + rows.Count > MaxEntries)
        {
            try { pane.Clear(); } catch { } // also drops the task items this pane owns
            _entries = 0;
        }

        foreach (var r in rows)
        {
            // MSBuild's canonical "file(line): message" shape: it reads naturally, greps cleanly, and is
            // what anyone scanning an Output pane already expects.
            string detail = r.Rewritten
                ? $"whole file, {r.TotalLines} lines"
                : (r.ChangeLabel.Length > 0 ? r.ChangeLabel : "changed");
            string text = $"{r.FilePath}({r.StartLine + 1}): {detail}";

            try
            {
                pane.OutputTaskItemString(
                    text + Environment.NewLine,
                    VSTASKPRIORITY.TP_NORMAL,
                    VSTASKCATEGORY.CAT_MISC,
                    "Claude Code",
                    (int)_vstaskbitmap.BMP_COMPILE,
                    r.FilePath,
                    (uint)Math.Max(0, r.StartLine), // task items are 0-based, like the rest of the protocol
                    text);
                _entries++;
            }
            catch (Exception e)
            {
                PLog.Warn($"could not log the edit to {r.FilePath}: {e.Message}");
                return; // a broken pane will not fix itself mid-loop
            }
        }

        // The task items queued above are BUFFERED until this call. Without it they never materialize -
        // and since the line-to-task association is exactly what double-click follows, the output lines
        // stay inert text. This is the single reason the pane's links did nothing when first shipped.
        try { pane.FlushToTaskList(); }
        catch (Exception e) { PLog.Warn($"could not flush the Edits task items: {e.Message}"); }
    }

    private static IVsOutputWindowPane? EnsurePane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane is not null) return _pane;
        try
        {
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow)) is not IVsOutputWindow ow)
                return null;
            var guid = PaneGuid;
            // Keeps the brand as a prefix so vs_read_output's "Claude Code" substring match still finds it.
            // fClearWithSolution: edits are workspace-scoped, so a new solution should start a fresh log.
            ow.CreatePane(ref guid, "Claude Code Edits", fInitVisible: 1, fClearWithSolution: 1);
            ow.GetPane(ref guid, out var pane);
            _pane = pane;
            return _pane;
        }
        catch (Exception e)
        {
            PLog.Warn($"could not create the Edits output pane: {e.Message}");
            return null;
        }
    }
}
