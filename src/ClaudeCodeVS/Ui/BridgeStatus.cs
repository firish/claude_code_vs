using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// Process-wide, UI-agnostic snapshot of the bridge for the dockable panel: endpoint, connection
/// state, edit stats, the set of pending diffs, a bounded curated log buffer, and a launch hook.
/// BridgeHost feeds it; the tool-window control reads it and subscribes to the events. Static because
/// the tool window is created lazily by VS, separate from BridgeHost.
/// </summary>
internal static class BridgeStatus
{
    /// <summary>One curated log line plus its level (so the panel can filter/colour it).</summary>
    public readonly struct LogEntry
    {
        public LogEntry(LogLevel level, string text) { Level = level; Text = text; }
        public LogLevel Level { get; }
        public string Text { get; }
    }

    private const int MaxLines = 500;
    private static readonly object Gate = new();
    private static readonly List<LogEntry> Lines = new();
    private static readonly Dictionary<string, string> PendingDiffs = new(); // diff id -> file path

    public static int? Port { get; private set; }
    public static string? Workspace { get; private set; }
    public static bool Connected { get; private set; }

    /// <summary>When the CLI most recently connected (for an uptime readout); null while disconnected.</summary>
    public static DateTime? ConnectedSince { get; private set; }

    /// <summary>
    /// True when the CLI's IDE WebSocket connected but our PULL MCP servers (vs-debug / vs-semantic /
    /// tests) never handshook within the grace window - i.e. those tools didn't load for this session
    /// (usually: Claude was launched outside the workspace folder, or the project MCP servers weren't
    /// approved). BridgeHost's connect/MCP-activity watcher sets it; the panel renders an amber banner
    /// with the remedy. Cleared the moment any /mcp activity arrives (a late approval) or the CLI drops.
    /// </summary>
    public static bool ToolsWarning { get; private set; }

    public static int EditsAccepted { get; private set; }
    public static int EditsRejected { get; private set; }

    /// <summary>Session-level debugger attribution: how often Claude inspected runtime state vs. drove execution.</summary>
    public static int DebugInspects { get; private set; } // vs_debug_state / evaluate / expand / threads / …
    public static int DebugDrives { get; private set; }   // continue / step / breakpoints / start-stop / …

    /// <summary>Token counts + estimated cost; used for both the latest call and the cumulative session.</summary>
    public readonly struct Usage
    {
        public Usage(long input, long output, long cacheRead, double costUsd)
        { Input = input; Output = output; CacheRead = cacheRead; CostUsd = costUsd; }
        public long Input { get; }
        public long Output { get; }
        public long CacheRead { get; }
        public double CostUsd { get; }
    }

    // Usage parsed from the CLI transcript on each edit/turn. Cost is an estimate.
    public static bool HasUsage { get; private set; }
    public static Usage Session { get; private set; } // cumulative across the whole conversation transcript
    public static Usage Latest { get; private set; }  // the most recent assistant API call
    public static int Turns { get; private set; }
    public static string? Model { get; private set; }

    public static void SetUsage(Usage session, Usage latest, int turns, string? model)
    {
        Session = session; Latest = latest; Turns = turns; Model = model;
        HasUsage = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Run-wild: when true, the permission gate auto-allows edits without opening the diff. In-memory
    /// only (resets each VS session) so it's never silently left on.
    /// </summary>
    public static bool AutoAcceptEdits { get; private set; }

    public static void SetAutoAcceptEdits(bool value)
    {
        if (AutoAcceptEdits == value) return;
        AutoAcceptEdits = value;
        Changed?.Invoke();

        // Checked MID-SESSION: the bridge side now auto-allows edits, but a running CLI session's own
        // mode cannot be changed from out here - so hand the user the lever instead of leaving the
        // mismatch silent. (Checked before Launch, the session starts in acceptEdits and none of this
        // applies; a permissive session locks the checkbox, so value=true implies mode is default.)
        if (value && Connected && !CliEditsPreApproved)
        {
            Notifier.Tip(Strings.TipRunWildOn);
        }
    }

    /// <summary>
    /// Allow Claude to DRIVE the debugger (Phase 3): continue/step, run-to-line, and set/remove
    /// breakpoints via the vs_continue/vs_step_*/vs_*_breakpoint tools. In-memory only (resets each VS
    /// session) so model-controlled execution is never silently left on - same safety model as
    /// <see cref="AutoAcceptEdits"/>. READS (vs_debug_state, vs_evaluate, …) are NOT gated; only
    /// execution control and breakpoint mutation are.
    /// </summary>
    public static bool AllowDebuggerDrive { get; private set; }

    public static void SetAllowDebuggerDrive(bool value)
    {
        if (AllowDebuggerDrive == value) return;
        AllowDebuggerDrive = value;
        Changed?.Invoke();
    }

    /// <summary>
    /// Allow Claude to CAPTURE pixels via the vs_capture_* tools: the debugged app's window, a window
    /// addressed by title (e.g. the browser showing your site), or the screen. ALL of it is gated - not
    /// just full-screen - because a title-addressed or screen capture can sweep up anything visible on
    /// the desktop. In-memory only (resets each VS session), same safety model as
    /// <see cref="AllowDebuggerDrive"/>. Every capture is feed-logged and staged as a visible attachment
    /// chip, so there is always an audit trail of exactly what Claude saw.
    /// </summary>
    public static bool AllowScreenCapture { get; private set; }

    public static void SetAllowScreenCapture(bool value)
    {
        if (AllowScreenCapture == value) return;
        AllowScreenCapture = value;
        Changed?.Invoke();
    }

    /// <summary>
    /// A Claude session's hooks are reaching the bridge while the IDE WebSocket has NEVER connected
    /// this VS session - the fingerprint of `claude` launched outside the extension (workspace hooks
    /// loaded, IDE channel never dialed). Drives the panel's "run /ide" banner; cleared on connect.
    /// </summary>
    public static bool HooksOnlyWarning { get; private set; }

    public static void SetHooksOnlyWarning(bool value)
    {
        if (HooksOnlyWarning == value) return;
        HooksOnlyWarning = value;
        Changed?.Invoke();
    }

    /// <summary>
    /// The CLI session's own permission mode, as observed on the most recent edit-permission request
    /// (see <see cref="IsPreApprovingMode"/> for the vocabulary; null = unknown / no session yet).
    /// Drives the run-wild checkbox's reflected state: while the CLI pre-approves edits, the checkbox
    /// shows checked and DISABLED - we cannot re-gate what the user already approved at the CLI level,
    /// so the UI must not pretend otherwise. Cleared on disconnect.
    /// </summary>
    public static string? CliPermissionMode { get; private set; }

    /// <summary>
    /// Does this CLI permission mode pre-approve file edits? The CLI's vocabulary GREW after our first
    /// pass at issue #17 - shift+tab's "auto mode" reports <c>auto</c>, and <c>dontAsk</c> joined it -
    /// so matching only acceptEdits/bypassPermissions silently re-gated sessions the user had already
    /// waved through (issue #38, live on CLI 2.1.229).
    ///
    /// Deliberately an ALLOW-LIST, not "anything that isn't default": an unrecognized mode gates (the
    /// user can still accept in the diff, so the safe failure is the visible one) and logs a warning,
    /// so the next vocabulary change surfaces as a report instead of a silent auto-allow.
    /// Current values: default (shown as "Manual" in the CLI UI) and plan gate; acceptEdits, auto,
    /// dontAsk and bypassPermissions pre-approve.
    /// </summary>
    public static bool IsPreApprovingMode(string? mode) =>
        mode is "acceptEdits" or "auto" or "dontAsk" or "bypassPermissions";

    /// <summary>True when a mode is one we recognize at all (unknown ones are logged once by the gate).</summary>
    public static bool IsKnownMode(string? mode) =>
        string.IsNullOrEmpty(mode) || mode is "default" or "plan" || IsPreApprovingMode(mode);

    /// <summary>True while the CLI session itself pre-approves edits.</summary>
    public static bool CliEditsPreApproved => IsPreApprovingMode(CliPermissionMode);

    public static void SetCliPermissionMode(string? mode)
    {
        if (CliPermissionMode == mode) return;
        CliPermissionMode = mode;
        Changed?.Invoke();
    }

    /// <summary>
    /// In-IDE notifications ("Claude finished responding" / "Claude needs your input"): the main-window
    /// InfoBar + taskbar flash the <see cref="Notifier"/> raises from the Stop and Notification hooks.
    /// Default ON - it's a convenience, not a safety gate - and in-memory like the other toggles, so the
    /// panel checkbox is the whole story for a session.
    /// </summary>
    public static bool NotifyEnabled { get; private set; } = true;

    public static void SetNotifyEnabled(bool value)
    {
        if (NotifyEnabled == value) return;
        NotifyEnabled = value;
        Changed?.Invoke();
    }

    /// <summary>Set by BridgeHost so the panel's Launch button can start the CLI.</summary>
    public static Func<Task>? LaunchAction { get; set; }

    /// <summary>Set by BridgeHost: start the CLI in a standalone external console (skips the docked native
    /// terminal - for users who want claude in its own window, or one that survives closing VS).</summary>
    public static Func<Task>? LaunchExternalAction { get; set; }

    /// <summary>Set by BridgeHost so the "hooks & tools didn't load" banner can start a correctly-pinned
    /// session even though one is already connected - bypasses LaunchAction's already-connected guard.</summary>
    public static Func<Task>? RelaunchAction { get; set; }

    /// <summary>Set by BridgeHost so the panel can bring the verbose Output pane forward (UI thread).</summary>
    public static Action? ShowOutputAction { get; set; }

    /// <summary>Set by BridgeHost: focus the claude session's input (docked tab or external console)
    /// so Enter sends what a context action just pushed into the composer. Best-effort.</summary>
    public static Func<Task>? FocusClaudeAction { get; set; }

    /// <summary>Fired when status/stats/pending change.</summary>
    public static event Action? Changed;

    /// <summary>Fired for each new log line (with its level so the panel can filter).</summary>
    public static event Action<LogLevel, string>? Logged;

    public static IReadOnlyList<LogEntry> LogSnapshot()
    {
        lock (Gate) return Lines.ToArray();
    }

    public static IReadOnlyList<string> PendingSnapshot()
    {
        lock (Gate) return new List<string>(PendingDiffs.Values);
    }

    public static void SetEndpoint(int port, string? workspace)
    {
        Port = port;
        Workspace = workspace;
        Changed?.Invoke();
    }

    public static void SetWorkspace(string? workspace)
    {
        Workspace = workspace;
        Changed?.Invoke();
    }

    public static void SetConnected(bool connected)
    {
        Connected = connected;
        ConnectedSince = connected ? DateTime.Now : null;
        Changed?.Invoke();
    }

    /// <summary>Set by BridgeHost's MCP-load watcher; see <see cref="ToolsWarning"/>. No-ops when unchanged.</summary>
    public static void SetToolsWarning(bool value)
    {
        if (ToolsWarning == value) return;
        ToolsWarning = value;
        Changed?.Invoke();
    }

    /// <summary>Record an edit decision for the stats strip.</summary>
    public static void RecordDecision(bool accepted)
    {
        if (accepted) EditsAccepted++; else EditsRejected++;
        Changed?.Invoke();
    }

    /// <summary>Record a debugger READ (vs_debug_state, vs_evaluate, vs_expand, vs_threads, …) for the stats strip.</summary>
    public static void RecordDebugInspect() { DebugInspects++; Changed?.Invoke(); }

    /// <summary>Record a debugger DRIVE (continue/step/run-to/breakpoints/start-stop/freeze/set-next) for the stats strip.</summary>
    public static void RecordDebugDrive() { DebugDrives++; Changed?.Invoke(); }

    // ---------------- Edits this turn (issue #44) ----------------

    private const int MaxTurnEdits = 60;
    private static readonly List<Edits.EditEntry> TurnEditList = new();
    private static bool _editsStale; // a new turn began; the list still shows the last one until it edits

    /// <summary>Jump targets for the changes Claude made in the current turn, newest last.</summary>
    public static IReadOnlyList<Edits.EditEntry> TurnEdits
    {
        get { lock (Gate) return TurnEditList.ToArray(); }
    }

    /// <summary>
    /// Append the rows for one applied edit. If a new turn has started since the last edit, the previous
    /// turn's rows are replaced rather than appended to - deliberately done HERE and not at prompt-submit,
    /// so the list survives while you are typing the next message, which is exactly when you might still
    /// be reading it.
    /// </summary>
    public static void RecordEdits(IReadOnlyList<Edits.EditEntry> rows)
    {
        if (rows is null || rows.Count == 0) return;
        lock (Gate)
        {
            if (_editsStale) { TurnEditList.Clear(); _editsStale = false; }
            TurnEditList.AddRange(rows);
            if (TurnEditList.Count > MaxTurnEdits)
                TurnEditList.RemoveRange(0, TurnEditList.Count - MaxTurnEdits);
        }
        Changed?.Invoke();
    }

    /// <summary>A new turn began (UserPromptSubmit). The next edit replaces the list; until then it stands.</summary>
    public static void MarkTurnStale()
    {
        lock (Gate)
        {
            if (TurnEditList.Count == 0 || _editsStale) return;
            _editsStale = true;
        }
    }

    public static void ClearEdits()
    {
        lock (Gate)
        {
            if (TurnEditList.Count == 0) return;
            TurnEditList.Clear();
            _editsStale = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Track a diff awaiting the user's decision (shown in the pending list).</summary>
    public static void AddPending(string id, string filePath)
    {
        lock (Gate) PendingDiffs[id] = filePath;
        Changed?.Invoke();
    }

    public static void RemovePending(string id)
    {
        bool removed;
        lock (Gate) removed = PendingDiffs.Remove(id);
        if (removed) Changed?.Invoke();
    }

    public static void Append(LogLevel level, string message)
    {
        var line = $"[{level.ToString().ToLowerInvariant()}] {message}";
        lock (Gate)
        {
            Lines.Add(new LogEntry(level, line));
            if (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
        Logged?.Invoke(level, line);
    }
}
