using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// Process-wide, UI-agnostic snapshot of the bridge for the dockable panel: endpoint, connection
/// state, a bounded log buffer, and a launch hook. BridgeHost feeds it; the tool-window control reads
/// it and subscribes to the events. Static because the tool window is created lazily by VS, separate
/// from BridgeHost.
/// </summary>
internal static class BridgeStatus
{
    private const int MaxLines = 500;
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    public static int? Port { get; private set; }
    public static string? Workspace { get; private set; }
    public static bool Connected { get; private set; }

    /// <summary>Set by BridgeHost so the panel's Launch button can start the CLI.</summary>
    public static Func<Task>? LaunchAction { get; set; }

    /// <summary>Fired when Port/Workspace/Connected change.</summary>
    public static event Action? Changed;

    /// <summary>Fired for each new log line.</summary>
    public static event Action<string>? Logged;

    public static IReadOnlyList<string> LogSnapshot()
    {
        lock (Gate) return Lines.ToArray();
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
        Changed?.Invoke();
    }

    public static void Append(LogLevel level, string message)
    {
        var line = $"[{level.ToString().ToLowerInvariant()}] {message}";
        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
        Logged?.Invoke(line);
    }
}
