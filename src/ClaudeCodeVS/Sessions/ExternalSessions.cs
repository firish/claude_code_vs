using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using ClaudeCodeVs.Protocol;

namespace ClaudeCodeVs.Sessions;

/// <summary>
/// Sessions held open by ANY running CLI on this machine, not just the ones we launched or that report to
/// our bridge. Found by reading the command lines of running claude processes for a resumed session id:
/// the VS Code extension starts <c>claude.exe ... --resume=&lt;id&gt;</c>, our launcher and a user typing
/// it use <c>--resume &lt;id&gt;</c> / <c>-r &lt;id&gt;</c>. Exists because deleting a session another
/// client still holds does not stick: that CLI re-writes its state (cost-state, ai-title, mode, ...) into
/// the transcript path when it exits, leaving a message-less stub behind (seen live).
/// Limit: a session switched to with <c>/resume</c> INSIDE a running CLI has no trace on the command line.
/// WMI costs tens to hundreds of ms, so callers scan off the UI thread and read the last result on it.
/// </summary>
internal static class ExternalSessions
{
    private static readonly Regex ResumeArg = new(
        @"(?:--resume|(?<![\w-])-r)(?:=|\s+)""?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static HashSet<string> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-read the running processes. Never throws; off the UI thread.</summary>
    public static HashSet<string> Scan()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Native installs run claude.exe; npm installs run node.exe with the CLI script.
            const string query = "SELECT CommandLine FROM Win32_Process WHERE Name = 'claude.exe' " +
                                 "OR (Name = 'node.exe' AND CommandLine LIKE '%claude%')";
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementBaseObject process in results)
            {
                using (process)
                {
                    if (process["CommandLine"] is not string commandLine) continue;
                    foreach (Match m in ResumeArg.Matches(commandLine))
                        ids.Add(m.Groups["id"].Value);
                }
            }
        }
        catch (Exception e)
        {
            Log.Event($"sessions: couldn't scan running claude processes ({e.Message})");
        }
        lock (Gate) _last = ids;
        return ids;
    }

    /// <summary>Was <paramref name="sessionId"/> open in some CLI as of the last <see cref="Scan"/>?</summary>
    public static bool IsOpen(string sessionId)
    {
        lock (Gate) return _last.Contains(sessionId);
    }

    /// <summary>The ids found by the last <see cref="Scan"/> (a copy).</summary>
    public static HashSet<string> LastScan()
    {
        lock (Gate) return new HashSet<string>(_last, StringComparer.OrdinalIgnoreCase);
    }
}
