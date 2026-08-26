using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// Reads text out of the Visual Studio Output window's panes. This is the one surface in the IDE that the
/// CLI genuinely cannot see: under F5 the debuggee's Console/ILogger output, first-chance exception
/// notices, assembly-binding failures and Hot Reload messages all land in the <b>Debug</b> pane, not in
/// any terminal Claude owns; MSBuild's full log lands in the <b>Build</b> pane. Backs the
/// <c>vs_read_output</c> tool and the raw-log half of <c>vs_build</c>.
///
/// Addressing is GUID-first for the well-known panes on purpose: pane display names are LOCALIZED (the
/// Build pane is "生成" in a zh-Hans VS), so a name-only match would work in English and nowhere else.
/// Anything without a stable GUID (Tests, our own "Claude Code" pane, other extensions') matches by name.
///
/// Every method here touches EnvDTE and must run on the UI thread (convention #1).
/// </summary>
internal static class OutputWindowReader
{
    /// <summary>Aliases the model can pass for the panes that have a documented, stable GUID.</summary>
    private static readonly Dictionary<string, Guid> WellKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        ["build"] = VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid,
        ["debug"] = VSConstants.OutputWindowPaneGuid.DebugPane_guid,
        ["general"] = VSConstants.OutputWindowPaneGuid.GeneralPane_guid,
    };

    /// <summary>The alias names always worth advertising, even before VS has created the pane.</summary>
    public static IEnumerable<string> WellKnownAliases => WellKnown.Keys;

    private static OutputWindowPanes? Panes()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // ToolWindows is a DTE2 member (EnvDTE80), not a DTE one.
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as EnvDTE80.DTE2;
            // ToolWindows.OutputWindow materializes the Output tool window if it isn't up yet. In practice
            // it always is - the extension writes its own log to a pane - so this is not a surprise-focus risk.
            return dte?.ToolWindows?.OutputWindow?.OutputWindowPanes;
        }
        catch { return null; }
    }

    /// <summary>Display names of the panes VS has actually created. UI thread.</summary>
    public static List<string> ListPaneNames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var names = new List<string>();
        var panes = Panes();
        if (panes == null) return names;
        try
        {
            foreach (OutputWindowPane p in panes)
                try { names.Add(p.Name); } catch { }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Resolve a pane by alias ("build"/"debug"/"general" - matched by GUID, so localization-proof) or by
    /// display name (exact first, then case-insensitive substring). Null when VS has not created it yet -
    /// the Build pane does not exist until the first build. UI thread.
    /// </summary>
    public static OutputWindowPane? Find(string alias)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(alias)) return null;
        var panes = Panes();
        if (panes == null) return null;

        try
        {
            if (WellKnown.TryGetValue(alias.Trim(), out var wanted))
            {
                foreach (OutputWindowPane p in panes)
                {
                    try { if (Guid.TryParse(p.Guid, out var g) && g == wanted) return p; }
                    catch { }
                }
            }

            foreach (OutputWindowPane p in panes)
                try { if (string.Equals(p.Name, alias, StringComparison.OrdinalIgnoreCase)) return p; } catch { }

            foreach (OutputWindowPane p in panes)
                try { if (p.Name?.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0) return p; } catch { }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Read the pane from <paramref name="fromLine"/> (1-based) to the end, keeping at most
    /// <paramref name="maxLines"/> lines from the END (the newest output is the interesting end of a log).
    /// Selects the range rather than SelectAll so a 50 MB diagnostic-verbosity build log never gets
    /// marshalled through COM in one string. UI thread.
    /// </summary>
    public static string ReadText(OutputWindowPane pane, int fromLine, int maxLines, out bool truncated, out int availableLines)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        truncated = false;
        availableLines = 0;
        try
        {
            var doc = pane.TextDocument;
            var end = doc?.EndPoint;
            if (doc == null || end == null) return "";

            int lastLine = end.Line;
            int lastCol = end.LineCharOffset;
            if (fromLine < 1) fromLine = 1;
            if (fromLine > lastLine) return "";

            availableLines = lastLine - fromLine + 1;
            int start = fromLine;
            if (maxLines > 0 && availableLines > maxLines)
            {
                start = lastLine - maxLines + 1;
                truncated = true;
            }

            var sel = doc.Selection;
            sel.MoveToLineAndOffset(start, 1);
            sel.MoveToLineAndOffset(lastLine, lastCol, true); // extend
            string text = sel.Text ?? "";
            sel.MoveToLineAndOffset(lastLine, lastCol);       // collapse: don't leave the pane fully selected
            return text;
        }
        catch { return ""; }
    }
}
