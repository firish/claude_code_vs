using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Activity;

/// <summary>
/// Follows the session transcript (the JSONL the CLI appends to as it works) while a turn is running and
/// turns its tool_use / tool_result lines into <see cref="AgentActivity"/> events. Polls rather than
/// watches: the file grows a few times a second at most, a 350ms poll is invisible, and
/// FileSystemWatcher's delivery on a file another process holds open is not something to depend on.
///
/// Reads are incremental from the last offset with a stateful UTF-8 decoder, so a line split across two
/// reads (or a multi-byte character split across two chunks) is reassembled rather than dropped.
/// </summary>
internal static class TranscriptTail
{
    private const int PollMs = 350;
    private static readonly TimeSpan MaxFollow = TimeSpan.FromHours(3); // safety net if Stop never arrives

    private static readonly object Gate = new();
    private static string? _path;
    private static long _offset;
    private static Decoder? _decoder;
    private static readonly StringBuilder Partial = new();
    private static CancellationTokenSource? _cts;
    private static bool _drain; // finish with one more read, then exit
    private static int _generation; // bumped by Follow; a read from an older follow must not commit its offset

    /// <summary>
    /// Start following <paramref name="transcriptPath"/> from its current end (a turn just began). A
    /// path change (a new session, /clear) re-targets; the same path while already following is a no-op.
    /// </summary>
    public static void Follow(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath)) return;
        CancellationToken token;
        lock (Gate)
        {
            bool samePath = string.Equals(_path, transcriptPath, StringComparison.OrdinalIgnoreCase);
            if (samePath && _cts is { IsCancellationRequested: false } && !_drain) return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _drain = false;
            _generation++;
            _path = transcriptPath;
            _decoder = Encoding.UTF8.GetDecoder();
            Partial.Clear();
            try { _offset = File.Exists(transcriptPath) ? new FileInfo(transcriptPath).Length : 0; }
            catch { _offset = 0; }
        }
        _ = Task.Run(() => LoopAsync(token), token);
    }

    /// <summary>The turn ended (Stop hook): read whatever landed last, then stop polling.</summary>
    public static void Stop()
    {
        lock (Gate) _drain = true;
    }

    /// <summary>The session is gone: stop outright.</summary>
    public static void Abort()
    {
        lock (Gate)
        {
            _cts?.Cancel();
            _cts = null;
            _path = null;
        }
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool drain;
                lock (Gate) drain = _drain;

                ReadNew();

                if (drain || DateTime.UtcNow - started > MaxFollow) break;
                await Task.Delay(PollMs, ct);
            }
        }
        catch (OperationCanceledException) { /* re-targeted or aborted */ }
        catch (Exception e)
        {
            Log.Warn($"activity: transcript tail stopped: {e.Message}");
        }
        finally
        {
            lock (Gate)
                if (_drain) { _cts = null; }
        }
    }

    private static void ReadNew()
    {
        string? path;
        long offset;
        Decoder? decoder;
        int generation;
        lock (Gate) { path = _path; offset = _offset; decoder = _decoder; generation = _generation; }
        if (path == null || decoder == null) return;

        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset)
            {
                // Shorter than where we left off: the file was replaced. Start over from its end.
                lock (Gate) { if (generation == _generation) { _offset = fs.Length; Partial.Clear(); } }
                return;
            }
            if (fs.Length == offset) return;
            fs.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[fs.Length - offset];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = fs.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
            lock (Gate)
            {
                // A Follow() raced this read (the drain of one turn overlapping the start of the next):
                // its offset is the truth now, and these bytes belong to the turn that just ended.
                if (generation != _generation) return;
                _offset = offset + read;
            }
        }
        catch (IOException) { return; }           // mid-write; next poll
        catch (UnauthorizedAccessException) { return; }

        var chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length)];
        int count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);

        // Split into complete lines; keep the unterminated tail for the next read.
        int lineStart = 0;
        for (int i = 0; i < count; i++)
        {
            if (chars[i] != '\n') continue;
            Partial.Append(chars, lineStart, i - lineStart);
            var line = Partial.ToString();
            Partial.Clear();
            lineStart = i + 1;
            ProcessLine(line);
        }
        if (lineStart < count) Partial.Append(chars, lineStart, count - lineStart);
    }

    private static void ProcessLine(string line)
    {
        if (line.Length < 20) return;
        // Cheap pre-filter: nearly every line is neither, and JSON parsing a 100KB Read result for nothing adds up.
        bool hasUse = line.IndexOf("\"tool_use\"", StringComparison.Ordinal) >= 0;
        bool hasResult = line.IndexOf("\"tool_result\"", StringComparison.Ordinal) >= 0;
        bool interrupted = line.IndexOf("[Request interrupted", StringComparison.Ordinal) >= 0;
        if (!hasUse && !hasResult && !interrupted) return;

        JObject o;
        try { o = JObject.Parse(line); }
        catch { return; }

        if ((bool?)o["isSidechain"] == true) return; // a subagent's own transcript, if it ever shares the file
        var type = (string?)o["type"];
        var content = o["message"]?["content"];

        if (type == "assistant" && content is JArray blocks)
        {
            foreach (var b in blocks)
            {
                if ((string?)b["type"] != "tool_use") continue;
                var id = (string?)b["id"];
                var name = (string?)b["name"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                AgentActivity.ToolStarted(id!, name!, b["input"] as JObject);
            }
        }
        else if (type == "user")
        {
            if (content is JArray results)
            {
                foreach (var r in results)
                {
                    if ((string?)r["type"] != "tool_result") continue;
                    var id = (string?)r["tool_use_id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    AgentActivity.ToolFinished(id!, o["toolUseResult"], ResultText(r["content"]));
                }
            }
            // Escape in the terminal: no Stop hook follows, so this is the end-of-turn signal.
            if (interrupted && TextOf(content).StartsWith("[Request interrupted", StringComparison.Ordinal))
                AgentActivity.TurnEnded();
        }
    }

    private static string? ResultText(JToken? content)
    {
        if (content == null) return null;
        if (content.Type == JTokenType.String) return (string?)content;
        if (content is JArray arr)
            foreach (var b in arr)
                if ((string?)b["type"] == "text" && (string?)b["text"] is { } t) return t;
        return null;
    }

    private static string TextOf(JToken? content)
    {
        if (content == null) return "";
        if (content.Type == JTokenType.String) return (string?)content ?? "";
        return ResultText(content) ?? "";
    }
}
