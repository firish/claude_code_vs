using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Sessions;

/// <summary>One past CLI session of the current workspace.</summary>
internal sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime LastWriteLocal { get; set; }
}

/// <summary>
/// Reads the CLI's own session store: <c>~/.claude/projects/&lt;encoded workspace&gt;/&lt;sessionId&gt;.jsonl</c>.
/// The format is undocumented, so every read is best-effort and defensive - a session we can't parse still
/// lists (with its id as the title) and is still resumable. Titles: the CLI's <c>custom-title</c> (a /rename)
/// wins over its generated <c>ai-title</c>, which wins over the first real user prompt.
/// </summary>
internal static class SessionStore
{
    private const int MaxSessions = 50;
    private const int TitleMax = 80;

    private sealed class CacheEntry { public long Length; public DateTime Mtime; public string Title = ""; public bool HasMessages; }
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The CLI names the folder by replacing every non-alphanumeric character with '-'.</summary>
    internal static string EncodeWorkspace(string workspace)
    {
        var sb = new StringBuilder(workspace.Length);
        foreach (var c in workspace)
            sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
        return sb.ToString();
    }

    /// <summary>Tests point this at a scratch folder; null = the real ~/.claude.</summary>
    internal static string? ClaudeDirOverride { get; set; }

    private static string ClaudeDir =>
        ClaudeDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string? FindProjectDir(string workspace)
    {
        var root = Path.Combine(ClaudeDir, "projects");
        if (!Directory.Exists(root)) return null;
        var want = EncodeWorkspace(workspace.TrimEnd('\\', '/'));
        // Case-insensitive: VS launches with "C:", other tools with "c:" (the same drift as the trust-key twins).
        return Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The newest sessions of <paramref name="workspace"/>, most recent first. Never throws.</summary>
    public static List<SessionInfo> List(string? workspace)
    {
        var result = new List<SessionInfo>();
        try
        {
            if (string.IsNullOrEmpty(workspace)) return result;
            var dir = FindProjectDir(workspace!);
            if (dir == null) return result;

            var files = new DirectoryInfo(dir).EnumerateFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var f in files)
            {
                // A transcript with no user/assistant message is a stub (only metadata: cost-state, ai-title,
                // mode, session-owner - e.g. left by another client mirroring a title). `claude --resume` on it
                // answers "No conversation found with session ID" (seen live), so it isn't a session to offer.
                // The VS Code extension filters on the same thing (its hasMessages).
                var info = Read(f);
                if (!info.HasMessages) continue;
                result.Add(new SessionInfo
                {
                    Id = Path.GetFileNameWithoutExtension(f.Name),
                    Title = info.Title,
                    LastWriteLocal = f.LastWriteTime,
                });
                if (result.Count >= MaxSessions) break;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"sessions: listing failed ({e.Message})");
        }
        return result;
    }

    // ---- Rename / delete: the CLI's on-disk conventions (rename is shared with the VS Code extension) ----

    private static string? TranscriptPath(string? workspace, string id)
    {
        if (string.IsNullOrEmpty(workspace)) return null;
        var dir = FindProjectDir(workspace!);
        return dir == null ? null : Path.Combine(dir, id + ".jsonl");
    }

    /// <summary>
    /// Rename = append a <c>{"type":"custom-title"}</c> record to the transcript; the last one wins. This is
    /// exactly what the CLI's /rename and the VS Code extension write, and what both read back.
    /// </summary>
    public static bool Rename(string? workspace, string id, string title)
    {
        try
        {
            title = (title ?? "").Trim();
            var path = TranscriptPath(workspace, id);
            if (path == null || title.Length == 0 || !File.Exists(path)) return false;

            var line = new JObject { ["type"] = "custom-title", ["sessionId"] = id, ["customTitle"] = title }
                .ToString(Newtonsoft.Json.Formatting.None) + "\n";
            // ReadWrite share: the CLI may be appending to the same file. One write call = one whole line.
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new UTF8Encoding(false).GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"sessions: rename failed ({e.Message})");
            return false;
        }
    }

    /// <summary>
    /// Delete a session and everything the CLI keeps for it, keyed by its id under <c>~/.claude</c>:
    /// the transcript <c>projects/*/&lt;id&gt;.jsonl</c> (every project dir - the VS Code extension mirrors
    /// title stubs into worktree dirs), its sidecar folder <c>projects/*/&lt;id&gt;/</c> (subagent transcripts,
    /// tool results), <c>file-history/&lt;id&gt;/</c> (checkpoint snapshots for /rewind) and
    /// <c>session-env/&lt;id&gt;/</c>. Deliberately NOT touched: <c>history.jsonl</c> (the CLI's shared prompt
    /// recall, appended to concurrently by every session) and other transcripts that merely mention the id.
    /// Returns the number of items removed; partial failures are logged and the rest still goes.
    /// </summary>
    public static int Delete(string id)
    {
        // The id becomes a path segment: only the CLI's own id alphabet, never a separator or "..".
        if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[A-Za-z0-9][A-Za-z0-9-]*$")) return 0;

        var claude = ClaudeDir;
        var targets = new List<string>();
        var projects = Path.Combine(claude, "projects");
        if (Directory.Exists(projects))
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                targets.Add(Path.Combine(dir, id + ".jsonl"));
                targets.Add(Path.Combine(dir, id));
            }
        targets.Add(Path.Combine(claude, "file-history", id));
        targets.Add(Path.Combine(claude, "session-env", id));

        int removed = 0;
        foreach (var t in targets)
        {
            try
            {
                if (File.Exists(t)) { File.Delete(t); removed++; }
                else if (Directory.Exists(t)) { Directory.Delete(t, recursive: true); removed++; }
            }
            catch (Exception e)
            {
                Log.Warn($"sessions: couldn't delete {t} ({e.Message})");
            }
        }
        lock (Cache)
            foreach (var key in Cache.Keys.Where(k => Path.GetFileNameWithoutExtension(k) == id).ToList())
                Cache.Remove(key);
        Log.Info($"sessions: deleted session {id} ({removed} item(s))");
        return removed;
    }

    /// <summary>
    /// The conversation's last exchange for the SESSIONS tooltip: the last real user prompt and the last
    /// assistant text, each trimmed to one short line. Reads only the transcript's tail. Never throws.
    /// </summary>
    public static (string? Prompt, string? Answer) Preview(string? workspace, string id)
    {
        // Big enough to reach back past a long run of tool output to the turn's prompt.
        const int TailBytes = 2 * 1024 * 1024;
        const int MaxChars = 200;
        try
        {
            var path = TranscriptPath(workspace, id);
            if (path == null || !File.Exists(path)) return (null, null);
            string? prompt = null, answer = null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > TailBytes) fs.Seek(-TailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0 && line.IndexOf("tool_result", StringComparison.Ordinal) < 0)
                    prompt = PromptText(line) ?? prompt;
                else if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0)
                    answer = AssistantText(line) ?? answer;
            }
            return (Short(prompt, MaxChars), Short(answer, MaxChars));
        }
        catch { return (null, null); }
    }

    private static string? AssistantText(string json)
    {
        try
        {
            var o = JObject.Parse(json);
            if ((bool?)o["isSidechain"] == true) return null;
            if (o["message"]?["content"] is not JArray content) return null;
            var text = string.Join(" ", content.OfType<JObject>()
                .Where(b => (string?)b["type"] == "text")
                .Select(b => ((string?)b["text"] ?? "").Trim())
                .Where(t => t.Length > 0));
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    private static string? Short(string? text, int max)
    {
        if (text == null) return null;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    /// <summary>The session's display title, or null when it has none yet (never the bare id).</summary>
    public static string? TitleFor(string? workspace, string id)
    {
        try
        {
            var path = TranscriptPath(workspace, id);
            if (path == null || !File.Exists(path)) return null;
            var title = TitleOf(new FileInfo(path));
            return title == id ? null : title;
        }
        catch { return null; }
    }

    /// <summary>The session id is the transcript's file name (what the hooks report as transcript_path).</summary>
    public static string? IdFromTranscriptPath(string? transcriptPath)
        => string.IsNullOrEmpty(transcriptPath) ? null : Path.GetFileNameWithoutExtension(transcriptPath);

    private static string TitleOf(FileInfo f) => Read(f).Title;

    private static CacheEntry Read(FileInfo f)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(f.FullName, out var hit) && hit.Length == f.Length && hit.Mtime == f.LastWriteTimeUtc)
                return hit;
        }

        string title = "";
        bool hasMessages = true; // unreadable = still offered; the CLI is the judge then
        try { title = ReadTitle(f.FullName, out hasMessages); }
        catch (Exception e) { Log.Warn($"sessions: couldn't read {f.Name} ({e.Message})"); }
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(f.Name);

        var entry = new CacheEntry { Length = f.Length, Mtime = f.LastWriteTimeUtc, Title = title, HasMessages = hasMessages };
        lock (Cache) Cache[f.FullName] = entry;
        return entry;
    }

    private static string ReadTitle(string path, out bool hasMessages)
    {
        hasMessages = false;
        string? custom = null, ai = null, firstPrompt = null;
        // FileShare.ReadWrite: the CLI may be appending to this very file right now.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Cheap substring gates first: most lines are huge tool payloads we must not JSON-parse.
            if (line.IndexOf("\"custom-title\"", StringComparison.Ordinal) >= 0)
                custom = Str(line, "customTitle") ?? custom;
            else if (line.IndexOf("\"ai-title\"", StringComparison.Ordinal) >= 0)
                ai = Str(line, "aiTitle") ?? ai;
            else if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0)
            {
                hasMessages = true;
                if (firstPrompt == null) firstPrompt = PromptText(line);
            }
            else if (!hasMessages && line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0)
                hasMessages = true;
        }
        return Trim(custom ?? ai ?? firstPrompt ?? "");
    }

    private static string? Str(string json, string key)
    {
        try { return (string?)JObject.Parse(json)[key]; }
        catch { return null; }
    }

    private static string? PromptText(string json)
    {
        try
        {
            var o = JObject.Parse(json);
            if ((bool?)o["isSidechain"] == true || (bool?)o["isMeta"] == true) return null;
            var content = o["message"]?["content"];
            // Command wrappers, caveats, IDE-selection and system-reminder blocks all start with '<' and are
            // not what the user typed; an IDE-connected prompt often carries one BEFORE the typed text, so
            // take the first text block that isn't one, rather than giving up on the first block.
            static bool Typed(string? t) => !string.IsNullOrEmpty(t) && t![0] != '<';
            string? text = content is JValue v ? ((string?)v)?.Trim()
                : content is JArray a ? a.OfType<JObject>()
                    .Where(b => (string?)b["type"] == "text")
                    .Select(b => ((string?)b["text"])?.Trim())
                    .FirstOrDefault(Typed)
                : null;
            return Typed(text) ? text : null;
        }
        catch { return null; }
    }

    private static string Trim(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= TitleMax ? s : s.Substring(0, TitleMax - 1) + "…";
    }
}
