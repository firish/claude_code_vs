using System;
using System.IO;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVs.Diff;

/// <summary>
/// Renders one proposed edit in VS's native diff viewer (IVsDifferenceService) with an Accept/Reject
/// InfoBar across the top, and completes the parked <see cref="DiffDecisions"/> entry when the user
/// chooses (build-plan §5). The native diff window is a read-only *viewer* with no buttons of its
/// own, so the InfoBar is how the user acts. All members run on the UI thread.
/// </summary>
internal sealed class DiffSession : IVsInfoBarUIEvents
{
    private const string Accept = "Accept";
    private const string Reject = "Reject";
    private const string RejectWithReason = "Reject with feedback…";

    private readonly DiffDecisions _decisions;
    private readonly string _tabName;
    private readonly string _newPath;
    private readonly string _contents;
    private readonly string _tempPath;
    private readonly string? _ownedLeftTemp; // an empty left file we created for "new file" diffs
    private readonly bool _writeBack;         // false = review-only (hook/permission path: the CLI writes)
    private readonly IVsWindowFrame _frame;

    private IVsInfoBarUIElement? _infoBar;
    private uint _cookie;
    private bool _resolved;

    private DiffSession(DiffDecisions decisions, string tabName, string newPath, string contents,
                        string tempPath, string? ownedLeftTemp, bool writeBack, IVsWindowFrame frame)
    {
        _decisions = decisions;
        _tabName = tabName;
        _newPath = newPath;
        _contents = contents;
        _tempPath = tempPath;
        _ownedLeftTemp = ownedLeftTemp;
        _writeBack = writeBack;
        _frame = frame;
    }

    /// <summary>
    /// Open the diff window + InfoBar. Must be called on the UI thread. The decision is delivered
    /// asynchronously through <paramref name="decisions"/> when the user clicks Accept/Reject.
    /// </summary>
    public static void Open(string oldPath, string newPath, string contents, string tabName,
                            string tempPath, DiffDecisions decisions, bool writeBack = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var diff = (IVsDifferenceService?)ServiceProvider.GlobalProvider.GetService(typeof(SVsDifferenceService));
        if (diff is null)
            throw new InvalidOperationException("SVsDifferenceService unavailable");

        // The left (baseline) is the file on disk. For a brand-new file there's nothing to compare
        // against, so synthesize an empty left side.
        string? ownedLeftTemp = null;
        string leftMoniker = oldPath;
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath))
        {
            ownedLeftTemp = Path.Combine(Path.GetTempPath(), $"claudediff_empty_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(ownedLeftTemp, string.Empty);
            leftMoniker = ownedLeftTemp;
        }

        string fileName = Path.GetFileName(string.IsNullOrEmpty(newPath) ? leftMoniker : newPath);
        var frame = diff.OpenComparisonWindow2(
            leftFileMoniker: leftMoniker,
            rightFileMoniker: tempPath,
            caption: $"Claude Code: {fileName}",
            Tooltip: newPath,
            leftLabel: $"{fileName} (current)",
            rightLabel: $"{fileName} (Claude proposal)",
            inlineLabel: null,
            roles: null,
            grfDiffOptions: 0);

        if (frame is null)
            throw new InvalidOperationException("OpenComparisonWindow2 returned no window frame");

        var session = new DiffSession(decisions, tabName, newPath, contents, tempPath, ownedLeftTemp, writeBack, frame);
        DiffRegistry.Register(tabName, session);
        session.AttachInfoBar();
        frame.Show();
    }

    /// <summary>Close this diff in response to the CLI's close_tab/closeAllDiffTabs (reject if pending).</summary>
    public void CloseExternally()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Resolve(false);
    }

    private void AttachInfoBar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ErrorHandler.Failed(_frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var hostObj))
            || hostObj is not IVsInfoBarHost host)
        {
            Log.Warn("diff window has no InfoBar host; accept/reject UI unavailable");
            return;
        }

        var factory = (IVsInfoBarUIFactory?)ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory));
        if (factory is null)
        {
            Log.Warn("SVsInfoBarUIFactory unavailable; accept/reject UI unavailable");
            return;
        }

        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan($"Claude Code proposes changes to {Path.GetFileName(_newPath)}. ") },
            new[] { new InfoBarHyperlink(Accept), new InfoBarHyperlink(Reject), new InfoBarHyperlink(RejectWithReason) },
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);

        _infoBar = factory.CreateInfoBar(model);
        _infoBar.Advise(this, out _cookie);
        host.AddInfoBar(_infoBar);
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS raises this event on the UI thread
        var text = actionItem.Text;
        if (string.Equals(text, Accept, StringComparison.Ordinal))
        {
            Resolve(true);
        }
        else if (string.Equals(text, RejectWithReason, StringComparison.Ordinal))
        {
            var reason = Ui.ReasonDialog.Prompt(System.IO.Path.GetFileName(_newPath));
            Resolve(false, reason);
        }
        else
        {
            Resolve(false);
        }
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS raises this event on the UI thread
        // The user dismissed the InfoBar without choosing -> treat as reject so the CLI unblocks.
        if (_infoBar is not null)
            _infoBar.Unadvise(_cookie);
        Resolve(false);
    }

    private void Resolve(bool accepted, string? rejectReason = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_resolved) return;
        _resolved = true;
        DiffRegistry.Unregister(_tabName);

        if (accepted && _writeBack)
        {
            try
            {
                // If the file is open in an editor, update its buffer in place and save (no reload
                // prompt); otherwise write straight to disk.
                if (ClaudeCodeVs.Editor.RunningDocuments.TryReplaceOpenDocument(_newPath, _contents))
                    Log.Info($"openDiff: updated open editor buffer for {_newPath}");
                else
                {
                    File.WriteAllText(_newPath, _contents);
                    Log.Info($"openDiff: wrote {_newPath}");
                }
            }
            catch (Exception e)
            {
                Log.Error($"openDiff write-back to {_newPath} failed: {e.Message}");
                accepted = false;
            }
        }

        try { _frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { /* already gone */ }
        try { File.Delete(_tempPath); } catch { /* best effort */ }
        if (_ownedLeftTemp is not null)
            try { File.Delete(_ownedLeftTemp); } catch { /* best effort */ }

        _decisions.Resolve(_tabName, new DiffDecision(accepted, accepted ? null : rejectReason));
    }
}
