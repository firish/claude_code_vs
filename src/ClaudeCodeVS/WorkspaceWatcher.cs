using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVs;

/// <summary>
/// Keeps the lockfile's workspaceFolders in sync with what's open in VS. The bridge starts at
/// shell-init (before any solution/folder is open), so the lockfile initially reports no workspace and
/// the CLI's /ide can't match it to the current directory. When a solution opens (IVsSolutionEvents)
/// or a folder opens in Open-Folder mode (IVsSolutionEvents7), we rewrite the lockfile so /ide matches.
/// All callbacks fire on the UI thread.
/// </summary>
internal sealed class WorkspaceWatcher : IVsSolutionEvents, IVsSolutionEvents7, System.IDisposable
{
    private readonly IVsSolution _solution;
    private readonly Lockfile _lockfile;
    private uint _cookie;

    public WorkspaceWatcher(IVsSolution solution, Lockfile lockfile)
    {
        _solution = solution;
        _lockfile = lockfile;
    }

    public void Start()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _solution.AdviseSolutionEvents(this, out _cookie);
        RefreshFromSolutionInfo(); // pick up a solution/folder already open at load time
    }

    private void RefreshFromSolutionInfo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_solution.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK && !string.IsNullOrEmpty(dir))
            _lockfile.UpdateWorkspaceFolders(new[] { dir.TrimEnd('\\') });
    }

    // ---- IVsSolutionEvents (classic solutions) ----
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS raises solution events on the UI thread
        RefreshFromSolutionInfo();
        return VSConstants.S_OK;
    }
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    // ---- IVsSolutionEvents7 (Open Folder mode) ----
    public void OnAfterOpenFolder(string folderPath) => _lockfile.UpdateWorkspaceFolders(new[] { folderPath.TrimEnd('\\') });
    public void OnBeforeCloseFolder(string folderPath) { }
    public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
    public void OnAfterCloseFolder(string folderPath) { }
    public void OnAfterLoadAllDeferredProjects() { }

    public void Dispose()
    {
        if (_cookie == 0) return;
        if (!ThreadHelper.CheckAccess()) return; // off the UI thread: skip (VS tears these down anyway)
        // CheckAccess above guarantees the UI thread here; VSTHRD010 can't see that.
#pragma warning disable VSTHRD010
        _solution.UnadviseSolutionEvents(_cookie);
#pragma warning restore VSTHRD010
        _cookie = 0;
    }
}
