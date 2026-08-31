using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// Parent-process lookups, used by the edit gate to decide whether a hook POST came from the CLI session
/// that is actually connected to this bridge (issue #42: a second Claude session running in the same
/// folder tree - under VS Code, say - loads the same workspace hook and otherwise looks identical).
///
/// A hook process is a descendant of its CLI: roughly powershell -> bash -> claude. So the question
/// "is this POST mine" reduces to "is my connected CLI's pid an ANCESTOR of the pid that POSTed".
///
/// Toolhelp32 rather than WMI on purpose: one snapshot of the whole process table costs single-digit
/// milliseconds, where a Win32_Process query costs a few hundred - and the observer hooks fire on every
/// prompt and every turn end, so this sits on a hot path. The snapshot is cached briefly because a
/// single edit produces a burst of lookups.
/// </summary>
internal static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int MaxDepth = 24;                      // cycle/corruption guard; real chains are 2-5

    private static readonly object Gate = new();
    private static Dictionary<int, int>? _parents;
    private static DateTime _stamp = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>pid -> parent pid for every visible process. Null when the snapshot could not be taken.</summary>
    private static Dictionary<int, int>? Parents()
    {
        lock (Gate)
        {
            if (_parents is not null && DateTime.UtcNow - _stamp < Ttl) return _parents;

            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return _parents; // keep any previous snapshot
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                if (Process32FirstW(snap, ref e))
                {
                    do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                    while (Process32NextW(snap, ref e));
                }
            }
            catch { return _parents; }
            finally { CloseHandle(snap); }

            if (map.Count == 0) return _parents;
            _parents = map;
            _stamp = DateTime.UtcNow;
            return _parents;
        }
    }

    /// <summary>
    /// True when <paramref name="ancestorPid"/> is <paramref name="pid"/> itself or any of its ancestors.
    /// False on an unreadable process table, so callers must treat false as "unknown" and fall back
    /// rather than as proof of foreignness.
    ///
    /// Caveat: pids are recycled, so in principle a stale parent link could produce a wrong match. The
    /// window is the seconds between a hook starting and its POST landing, and the consequence is showing
    /// a diff for an edit rather than deferring it, so this is not worth defending against further.
    /// </summary>
    public static bool IsSelfOrAncestor(int ancestorPid, int pid)
    {
        if (ancestorPid <= 0 || pid <= 0) return false;
        if (ancestorPid == pid) return true;

        var parents = Parents();
        if (parents is null) return false;

        int current = pid;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (!parents.TryGetValue(current, out int parent) || parent <= 0 || parent == current) return false;
            if (parent == ancestorPid) return true;
            current = parent;
        }
        return false;
    }

    /// <summary>True when the process table could be read at all - lets callers distinguish "not mine" from "can't tell".</summary>
    public static bool Available => Parents() is not null;
}
