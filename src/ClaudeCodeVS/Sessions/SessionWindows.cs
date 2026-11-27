using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeCodeVs.Sessions;

/// <summary>
/// Which CLI session is open where. Lets the Sessions card's "Switch to session" FOCUS the window that
/// already hosts a session instead of opening a second terminal on the same transcript.
///
/// Two facts are joined here. (1) Identity: every owned hook POST carries the session's transcript (its file
/// name is the session id) and the hook's pid, which sits under the CLI's pid - so we learn
/// <c>sessionId -> CLI pid</c>. (2) Windows: when WE launch a terminal we hold a focus action for it (a VS
/// Terminal tab guid or an external console process). ITerminalService can't tell us which pid lives in
/// which tab, so the two are joined by timing: a launch is "pending" until the first hook from a CLI nobody
/// has claimed arrives, and a <c>--resume &lt;id&gt;</c> launch is optimistically bound to its id at once so a
/// second double-click before the first prompt doesn't open a duplicate.
/// A session someone else started (their own terminal) is live but has no focus action: we can say it is
/// already open, not bring it forward.
/// </summary>
internal static class SessionWindows
{
    private static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(3);
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();

    private sealed class Entry
    {
        public string? Id;
        public int? CliPid;
        public Func<Task<bool>>? Focus;
        public DateTime LaunchedUtc;
        /// <summary>Retitles the window (UI thread); null when we can't (external console, unknown frame).</summary>
        public Func<string, bool>? SetTitle;
        public string? AppliedTitle;
        /// <summary>A terminal WE launched (vs. a session first seen through a hook).</summary>
        public bool Ours;
    }

    /// <summary>What the caller should do for a session.</summary>
    internal enum Presence { NotOpen, OpenFocusable, OpenElsewhere }

    /// <summary>
    /// A launch is starting (<paramref name="resumeId"/> = the session it resumes, or null). Registered
    /// BEFORE the terminal is created on purpose: the CLI can start and send `ide_connected` while the
    /// launcher is still settling the tab (frame lookup, focus), and that pid must find this entry to claim
    /// - otherwise the entry stays pid-less, ages out, and an open session reads as closed.
    /// Finish with <see cref="CompleteLaunch"/> or <see cref="CancelLaunch"/>.
    /// </summary>
    public static object BeginLaunch(string? resumeId)
    {
        var entry = new Entry { Id = resumeId, LaunchedUtc = DateTime.UtcNow, Ours = true };
        lock (Gate)
        {
            Prune();
            if (resumeId != null) Entries.RemoveAll(e => e.Id == resumeId && !e.Ours && e.Focus == null && e.CliPid == null);
            Entries.Add(entry);
        }
        return entry;
    }

    /// <summary>The terminal exists: attach how to focus (and optionally retitle) it.</summary>
    public static void CompleteLaunch(object handle, Func<Task<bool>> focus,
        Func<string, bool>? setTitle = null, string? initialTitle = null)
    {
        var e = (Entry)handle;
        lock (Gate)
        {
            e.Focus = focus;
            e.SetTitle = setTitle;
            e.AppliedTitle = initialTitle;
        }
    }

    /// <summary>The launch failed: forget it.</summary>
    public static void CancelLaunch(object handle)
    {
        lock (Gate) Entries.Remove((Entry)handle);
    }

    /// <summary>
    /// Bring every retitlable window in line with its session's current title (a /rename, a freshly
    /// generated ai-title, or a new session's first prompt). <paramref name="titleOf"/> runs off the UI
    /// thread and may read files; the returned actions must run on the UI thread.
    /// </summary>
    public static List<Action> PlanTitleUpdates(Func<string, string?> titleOf)
    {
        List<(Entry e, string id)> targets;
        lock (Gate)
        {
            Prune();
            targets = Entries.Where(e => e.Id != null && e.SetTitle != null).Select(e => (e, e.Id!)).ToList();
        }

        var actions = new List<Action>();
        foreach (var (e, id) in targets)
        {
            var title = titleOf(id);
            if (string.IsNullOrEmpty(title) || title == e.AppliedTitle) continue;
            actions.Add(() =>
            {
                if (e.SetTitle!(title!)) e.AppliedTitle = title;
                else e.SetTitle = null; // frame gone: stop trying
            });
        }
        return actions;
    }

    /// <summary>An owned hook reported <paramref name="sessionId"/> running under CLI <paramref name="cliPid"/>.</summary>
    public static void Observe(string sessionId, int cliPid)
    {
        lock (Gate)
        {
            Prune();
            // A CLI hosts one session at a time (/clear or /resume inside it switches): rebind that CLI's entry.
            var byPid = Entries.FirstOrDefault(e => e.CliPid == cliPid);
            if (byPid != null) { byPid.Id = sessionId; Dedupe(byPid); return; }

            // A launch of ours whose CLI we hadn't seen yet: same id (optimistic --resume) first, else the
            // newest unclaimed launch inside the window (a new session, or a resume that forked a new id).
            var now = DateTime.UtcNow;
            var claim = Entries.FirstOrDefault(e => e.CliPid == null && e.Id == sessionId)
                     ?? Entries.Where(e => e.CliPid == null && e.Ours && now - e.LaunchedUtc <= PendingWindow)
                               .OrderByDescending(e => e.LaunchedUtc).FirstOrDefault();
            if (claim != null) { claim.Id = sessionId; claim.CliPid = cliPid; Dedupe(claim); return; }

            Entries.Add(new Entry { Id = sessionId, CliPid = cliPid }); // started outside the extension
        }
    }

    /// <summary>
    /// A CLI connected to the bridge (its `ide_connected` pid). Binds it to our newest launch that has no CLI
    /// yet - so the entry's liveness is tracked from startup, before (or without) any prompt and hook.
    /// </summary>
    public static void CliConnected(int cliPid)
    {
        lock (Gate)
        {
            Prune();
            if (Entries.Any(e => e.CliPid == cliPid)) return;
            var now = DateTime.UtcNow;
            // OLDEST unclaimed launch first: CLIs connect in launch order, so two quick launches pair up
            // correctly (newest-first would hand the first CLI to the second launch).
            var claim = Entries.Where(e => e.CliPid == null && e.Ours && now - e.LaunchedUtc <= PendingWindow)
                               .OrderBy(e => e.LaunchedUtc).FirstOrDefault();
            if (claim != null) claim.CliPid = cliPid;
        }
    }

    /// <summary>
    /// The CLI's socket closed. Liveness is still judged by the PROCESS, not the socket: a CLI that merely
    /// reconnects keeps its entry (and its window), while one that exited is dropped by <see cref="Prune"/>.
    /// </summary>
    public static void CliDisconnected(int cliPid)
    {
        lock (Gate) Prune();
    }

    public static Presence Check(string sessionId)
    {
        lock (Gate)
        {
            Prune();
            var e = Entries.FirstOrDefault(x => x.Id == sessionId);
            if (e == null) return Presence.NotOpen;
            // Ours = a window we can (or, mid-launch, soon can) bring forward.
            return e.Ours ? Presence.OpenFocusable : Presence.OpenElsewhere;
        }
    }

    /// <summary>Bring the session's window forward. False (and the stale entry dropped) when that fails.</summary>
    public static async Task<bool> TryFocusAsync(string sessionId)
    {
        Func<Task<bool>>? focus;
        lock (Gate) focus = Entries.FirstOrDefault(x => x.Id == sessionId)?.Focus;
        if (focus == null) return false;

        bool ok;
        try { ok = await focus(); }
        catch { ok = false; }
        if (!ok)
            lock (Gate) Entries.RemoveAll(x => x.Id == sessionId);
        return ok;
    }

    private static void Dedupe(Entry keep)
        => Entries.RemoveAll(e => !ReferenceEquals(e, keep) && e.Id == keep.Id && e.Id != null);

    /// <summary>
    /// Drop entries whose CLI process has exited (their `cmd /K` window may linger, but the session is gone),
    /// and launches whose CLI never identified itself within the pending window (the launch failed, or the
    /// terminal was closed before claude started) - otherwise they'd read as "open" forever.
    /// </summary>
    private static void Prune()
    {
        var now = DateTime.UtcNow;
        Entries.RemoveAll(e => e.CliPid is int pid ? !IsAlive(pid) : now - e.LaunchedUtc > PendingWindow);
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
