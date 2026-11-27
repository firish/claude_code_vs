using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVs.Edits;

/// <summary>
/// "Show diff" for a row of the EDITS tab: opens an edit's before/after texts in VS's diff viewer, read-only,
/// with no Accept/Reject - the edit is already applied, this is a look back. Both sides are written to
/// temp files under %TEMP%\claude-codevs-editdiff\, which is pruned of anything older than a day each time.
/// UI thread.
/// </summary>
internal static class EditDiffViewer
{
    private const uint ReadOnlyTemporaryBoth =
        (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);

    public static bool Show(EditEntry edit)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (edit.Snapshot is not { } snapshot) return false;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-codevs-editdiff");
            Prune(root);
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            // Same file name on both sides (different folders) so the editor picks the right language.
            var left = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "before")).FullName, edit.FileName);
            var right = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "after")).FullName, edit.FileName);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(left, snapshot.Before, utf8);
            File.WriteAllText(right, snapshot.After, utf8);

            var diff = (IVsDifferenceService?)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
            if (diff == null) return false;
            var frame = diff.OpenComparisonWindow2(
                leftFileMoniker: left,
                rightFileMoniker: right,
                caption: string.Format(Strings.EditsDiffCaptionFormat, edit.FileName),
                Tooltip: edit.FilePath,
                leftLabel: string.Format(Strings.EditsDiffBeforeFormat, edit.FileName),
                rightLabel: string.Format(Strings.EditsDiffAfterFormat, edit.FileName),
                inlineLabel: null,
                roles: null,
                grfDiffOptions: ReadOnlyTemporaryBoth);
            frame?.Show();
            return frame != null;
        }
        catch (Exception e)
        {
            Log.Warn($"edits: couldn't show the diff for {edit.FilePath} ({e.Message})");
            return false;
        }
    }

    private static void Prune(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)))
            {
                try { dir.Delete(recursive: true); } catch { /* still open in a diff */ }
            }
        }
        catch { /* best-effort */ }
    }
}
