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

    public static int EditsAccepted { get; private set; }
    public static int EditsRejected { get; private set; }

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
    }

    /// <summary>Set by BridgeHost so the panel's Launch button can start the CLI.</summary>
    public static Func<Task>? LaunchAction { get; set; }

    /// <summary>Set by BridgeHost so the panel can bring the verbose Output pane forward (UI thread).</summary>
    public static Action? ShowOutputAction { get; set; }

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

    /// <summary>Record an edit decision for the stats strip.</summary>
    public static void RecordDecision(bool accepted)
    {
        if (accepted) EditsAccepted++; else EditsRejected++;
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
