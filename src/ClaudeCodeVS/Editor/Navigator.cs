using System;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// Open a document and reveal a line range. Extracted from the openFile tool so the panel's Edits list
/// jumps to a change through exactly the same path the CLI's own openFile takes - one behaviour to get
/// right, one place to fix it. Lines are 0-based throughout, matching the protocol and the Error List.
/// UI thread only.
/// </summary>
internal static class Navigator
{
    /// <summary>
    /// Open <paramref name="filePath"/> and, when a line is given, select and scroll to it.
    /// Returns false if the document could not be opened (deleted, renamed, never existed).
    /// </summary>
    public static bool OpenAt(string filePath, int? startLine = null, int? endLine = null, bool makeFrontmost = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(filePath)) return false;

        IVsWindowFrame frame;
        try
        {
            VsShellUtilities.OpenDocument(
                ServiceProvider.GlobalProvider,
                filePath,
                VSConstants.LOGVIEWID.TextView_guid,
                out _,
                out _,
                out frame);
        }
        catch (Exception e)
        {
            Log.Warn($"could not open {filePath}: {e.Message}");
            return false;
        }

        if (frame is null) return false;
        if (makeFrontmost) { try { frame.Show(); } catch { } }

        if (startLine.HasValue)
        {
            try
            {
                var view = VsShellUtilities.GetTextView(frame);
                if (view is not null)
                {
                    int sl = Math.Max(0, startLine.Value);
                    int el = Math.Max(sl, endLine ?? startLine.Value);
                    view.SetSelection(sl, 0, el, 0);
                    view.EnsureSpanVisible(new TextSpan { iStartLine = sl, iStartIndex = 0, iEndLine = el, iEndIndex = 0 });
                }
            }
            catch { /* opened, just not scrolled - still useful */ }
        }
        return true;
    }
}
