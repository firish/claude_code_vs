using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace ClaudeCodeVs.Editor;

/// <summary>One open document: its path and whether it has unsaved changes.</summary>
internal readonly struct OpenDoc
{
    public OpenDoc(string path, bool isDirty) { Path = path; IsDirty = isDirty; }
    public string Path { get; }
    public bool IsDirty { get; }
}

/// <summary>
/// Thin wrapper over the Running Document Table for the awareness tools (getOpenEditors,
/// checkDocumentDirty, saveDocument) and the RDT-aware diff write-back. All methods must be called on
/// the UI thread. The classic RDT API hands back doc-data as a raw IUnknown (IntPtr) that we must
/// Marshal.Release, so that bookkeeping lives here in one place.
/// </summary>
internal static class RunningDocuments
{
    private static IVsRunningDocumentTable? Rdt()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
    }

    /// <summary>
    /// All open editor tabs with their dirty state. Sourced from document *window frames*
    /// (IVsUIShell.GetDocumentWindowEnum), NOT the RDT — VS only adds a doc to the RDT once it's been
    /// activated, so background tabs are missing from the RDT but present as frames.
    /// </summary>
    public static List<OpenDoc> OpenDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<OpenDoc>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in DocumentFrames())
        {
            var moniker = FrameMoniker(frame);
            if (!LooksLikeFile(moniker) || !seen.Add(moniker!)) continue;
            result.Add(new OpenDoc(moniker!, FrameDirty(frame)));
        }
        return result;
    }

    /// <summary>Dirty state of an open document; null if it isn't open in any tab.</summary>
    public static bool? IsDirty(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var frame in DocumentFrames())
        {
            var moniker = FrameMoniker(frame);
            if (moniker != null && PathEquals(moniker, path))
                return FrameDirty(frame);
        }
        return null;
    }

    // ---- document-window-frame helpers ----

    private static List<IVsWindowFrame> DocumentFrames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var frames = new List<IVsWindowFrame>();
        var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) as IVsUIShell;
        if (shell == null || shell.GetDocumentWindowEnum(out IEnumWindowFrames en) != VSConstants.S_OK || en == null)
            return frames;
        var arr = new IVsWindowFrame[1];
        while (en.Next(1, arr, out uint fetched) == VSConstants.S_OK && fetched == 1)
            if (arr[0] != null) frames.Add(arr[0]);
        return frames;
    }

    private static string? FrameMoniker(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object o) == VSConstants.S_OK
            ? o as string
            : null;
    }

    private static bool FrameDirty(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out object o) == VSConstants.S_OK
            && o is IVsPersistDocData pdd
            && pdd.IsDocDataDirty(out int d) == VSConstants.S_OK
            && d != 0;
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Save an open document if dirty. Returns false if it isn't open.</summary>
    public static bool Save(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rdt = Rdt();
        if (rdt == null) return false;
        if (rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out IVsHierarchy hier, out uint itemid, out IntPtr docData, out uint cookie) != VSConstants.S_OK)
            return false;
        try
        {
            return rdt.SaveDocuments((uint)__VSRDTSAVEOPTIONS.RDTSAVEOPT_SaveIfDirty, hier, itemid, cookie) == VSConstants.S_OK;
        }
        finally { if (docData != IntPtr.Zero) Marshal.Release(docData); }
    }

    /// <summary>
    /// If the document is open in an editor, replace its buffer contents in place and save (so the
    /// visible editor updates and there's no "file changed on disk, reload?" prompt). Returns false if
    /// the document isn't open (caller should then write to disk directly).
    /// </summary>
    public static bool TryReplaceOpenDocument(string path, string contents)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rdt = Rdt();
        if (rdt == null) return false;
        if (rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out IVsHierarchy hier, out uint itemid, out IntPtr docData, out uint cookie) != VSConstants.S_OK
            || docData == IntPtr.Zero)
            return false;

        try
        {
            if (Marshal.GetObjectForIUnknown(docData) is not IVsTextBuffer vsBuffer)
                return false; // not a text document (e.g. a designer) — let the caller write to disk

            var cm = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            var adapters = cm?.GetService<IVsEditorAdaptersFactoryService>();
            var buffer = adapters?.GetDocumentBuffer(vsBuffer) ?? adapters?.GetDataBuffer(vsBuffer);
            if (buffer == null) return false;

            using (var edit = buffer.CreateEdit())
            {
                edit.Replace(0, buffer.CurrentSnapshot.Length, contents);
                edit.Apply();
            }

            rdt.SaveDocuments((uint)__VSRDTSAVEOPTIONS.RDTSAVEOPT_SaveIfDirty, hier, itemid, cookie);
            return true;
        }
        finally { Marshal.Release(docData); }
    }

    /// <summary>
    /// If the document is open and has no unsaved changes, reload it from disk so the editor reflects
    /// an external write (the CLI's edit in the single-gate path). Skips dirty docs to avoid clobbering
    /// unsaved work. Call on the UI thread.
    /// </summary>
    public static void ReloadIfClean(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rdt = Rdt();
        if (rdt == null) return;
        if (rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr docData, out _) != VSConstants.S_OK
            || docData == IntPtr.Zero)
            return;
        try
        {
            if (Marshal.GetObjectForIUnknown(docData) is IVsPersistDocData pdd)
            {
                if (pdd.IsDocDataDirty(out int dirty) == VSConstants.S_OK && dirty != 0)
                    return; // don't discard unsaved edits
                pdd.ReloadDocData(0);
            }
        }
        catch { /* best effort */ }
        finally { Marshal.Release(docData); }
    }

    /// <summary>True if the document is currently open in the RDT.</summary>
    public static bool IsOpen(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var rdt = Rdt();
        if (rdt == null) return false;
        if (rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, path, out _, out _, out IntPtr docData, out _) != VSConstants.S_OK)
            return false;
        if (docData != IntPtr.Zero) Marshal.Release(docData);
        return true;
    }

    private static bool LooksLikeFile(string? moniker)
        => !string.IsNullOrEmpty(moniker)
           && moniker!.IndexOfAny(Path.GetInvalidPathChars()) < 0
           && Path.IsPathRooted(moniker);
}
