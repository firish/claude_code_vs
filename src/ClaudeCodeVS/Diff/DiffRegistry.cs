using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Diff;

/// <summary>
/// Tracks currently-open diff windows by tab_name so the CLI's close_tab / closeAllDiffTabs can close
/// them. A session registers on open and unregisters when it resolves (accept/reject already closes
/// its own frame), so close_tab after an accept is a no-op; the registry matters for closing a still-
/// open diff and for clearing leftover diffs when the CLI reconnects (it calls closeAllDiffTabs then).
/// </summary>
internal static class DiffRegistry
{
    private static readonly ConcurrentDictionary<string, DiffSession> Open = new();

    public static void Register(string tabName, DiffSession session) => Open[tabName] = session;
    public static void Unregister(string tabName) => Open.TryRemove(tabName, out _);

    /// <summary>Close one open diff by tab_name (resolves it as rejected if still pending).</summary>
    public static async Task CloseTabAsync(string tabName)
    {
        if (!Open.TryGetValue(tabName, out var session)) return;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        session.CloseExternally();
    }

    /// <summary>Close every open diff (called on connect, and as a cleanup).</summary>
    public static async Task CloseAllAsync()
    {
        var sessions = Open.Values.ToArray();
        if (sessions.Length == 0) return;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        foreach (var s in sessions) s.CloseExternally();
    }
}
