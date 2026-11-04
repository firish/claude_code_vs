using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Activity;

/// <summary>
/// What Claude is doing RIGHT NOW, for the panel's activity card: whether a turn is running and for
/// how long, the tool call(s) in flight, and the session's task list. Static and UI-agnostic like
/// <see cref="Ui.BridgeStatus"/>; <see cref="TranscriptTail"/> feeds it, the panel reads it on
/// <see cref="Changed"/>.
///
/// Turn boundaries come from the two hooks the bridge already owns: UserPromptSubmit (a prompt was sent)
/// and Stop (Claude finished). Everything in between is read from the session transcript, which the CLI
/// appends to as it goes - a tool_use block lands in the file before the tool runs, and its tool_result
/// after - so the card follows the real work without a hook process per tool call (the alternative, a
/// PreToolUse+PostToolUse observer pair, would cost two PowerShell 5.1 spawns per tool). The transcript
/// format is undocumented, so parsing is defensive and any oddity degrades to "no detail", never to an
/// error the user sees.
/// </summary>
internal static class AgentActivity
{
    /// <summary>One tool call in flight (or the last one completed).</summary>
    public sealed class ToolCall
    {
        public ToolCall(string id, string name, string summary, DateTime startedAt)
        { Id = id; Name = name; Summary = summary; StartedAt = startedAt; }
        public string Id { get; }
        /// <summary>The tool's short name: "Edit", "Bash", "vs_build" (MCP prefix stripped).</summary>
        public string Name { get; }
        /// <summary>What it is acting on: the file, the command description, the query.</summary>
        public string Summary { get; }
        public DateTime StartedAt { get; }
    }

    /// <summary>One entry of Claude's own task list (TaskCreate/TaskUpdate, or the older TodoWrite).</summary>
    public sealed class TaskItem
    {
        public string Id = "";
        public string Subject = "";
        public string? ActiveForm; // present-tense label shown while in progress ("Running tests")
        public string Status = "pending"; // pending | in_progress | completed
    }

    private const int MaxTasks = 40;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ToolCall> Active = new(StringComparer.Ordinal);
    private static readonly List<TaskItem> TaskList = new();
    private static readonly Dictionary<string, TaskItem> PendingCreates = new(StringComparer.Ordinal); // tool_use id -> task awaiting its result

    public static bool TurnActive { get; private set; }
    public static DateTime? TurnStartedAt { get; private set; }
    public static DateTime? TurnEndedAt { get; private set; }
    /// <summary>First line of the prompt that started the current/last turn, trimmed for the card.</summary>
    public static string? PromptPreview { get; private set; }
    public static int ToolCallsThisTurn { get; private set; }
    /// <summary>The most recently completed tool call, shown while nothing is in flight.</summary>
    public static ToolCall? LastCompleted { get; private set; }
    /// <summary>True once any turn has been seen this session - the card stays hidden until then.</summary>
    public static bool HasActivity { get; private set; }

    public static IReadOnlyList<ToolCall> ActiveTools
    {
        get { lock (Gate) return Active.Values.OrderBy(t => t.StartedAt).ToArray(); }
    }

    public static IReadOnlyList<TaskItem> Tasks
    {
        get { lock (Gate) return TaskList.Select(Clone).ToArray(); }
    }

    private static TaskItem Clone(TaskItem t) => new() { Id = t.Id, Subject = t.Subject, ActiveForm = t.ActiveForm, Status = t.Status };

    /// <summary>Fired on any change; the panel marshals to its dispatcher.</summary>
    public static event Action? Changed;

    // ---------------- turn boundaries (from the hooks) ----------------

    public static void TurnStarted(string? promptPreview)
    {
        lock (Gate)
        {
            TurnActive = true;
            HasActivity = true;
            TurnStartedAt = DateTime.Now;
            TurnEndedAt = null;
            ToolCallsThisTurn = 0;
            LastCompleted = null;
            Active.Clear();
            PendingCreates.Clear();
            if (!string.IsNullOrWhiteSpace(promptPreview)) PromptPreview = promptPreview!.Trim();
        }
        Changed?.Invoke();
    }

    /// <summary>A hook fired mid-turn (an edit) with no UserPromptSubmit seen first: start tracking now.</summary>
    public static void EnsureTurn()
    {
        bool raise = false;
        lock (Gate)
        {
            if (!TurnActive)
            {
                TurnActive = true;
                HasActivity = true;
                TurnStartedAt = DateTime.Now;
                TurnEndedAt = null;
                ToolCallsThisTurn = 0;
                raise = true;
            }
        }
        if (raise) Changed?.Invoke();
    }

    public static void TurnEnded()
    {
        lock (Gate)
        {
            if (!TurnActive) return;
            TurnActive = false;
            TurnEndedAt = DateTime.Now;
            Active.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>The CLI went away: nothing is running and the task list belonged to that session.</summary>
    public static void SessionEnded()
    {
        lock (Gate)
        {
            TurnActive = false;
            TurnEndedAt ??= DateTime.Now;
            Active.Clear();
            TaskList.Clear();
            PendingCreates.Clear();
        }
        Changed?.Invoke();
    }

    // ---------------- transcript events (from TranscriptTail) ----------------

    /// <summary>A tool_use block landed in the transcript: the call is starting (or awaiting permission).</summary>
    public static void ToolStarted(string id, string rawName, JObject? input)
    {
        var name = ShortName(rawName);
        lock (Gate)
        {
            // Only the hooks open a turn. A tool_use that lands after Stop (the tail's final drain read)
            // must not resurrect a finished turn into a "Working" that never ends.
            if (!TurnActive) return;
            Active[id] = new ToolCall(id, name, Summarize(name, input), DateTime.Now);
            ToolCallsThisTurn++;
            ApplyTaskToolInput(id, rawName, input);
        }
        Changed?.Invoke();
    }

    /// <summary>Its tool_result landed: the call finished.</summary>
    public static void ToolFinished(string id, JToken? toolUseResult, string? resultText)
    {
        lock (Gate)
        {
            if (Active.TryGetValue(id, out var call))
            {
                Active.Remove(id);
                LastCompleted = call;
            }
            CompleteTaskCreate(id, toolUseResult, resultText);
        }
        Changed?.Invoke();
    }

    // ---------------- task list ----------------

    private static void ApplyTaskToolInput(string toolUseId, string rawName, JObject? input)
    {
        if (input == null) return;
        switch (rawName)
        {
            case "TodoWrite":
                // The legacy tool replaces the whole list every call.
                if (input["todos"] is not JArray todos) return;
                TaskList.Clear();
                int i = 0;
                foreach (var t in todos.OfType<JObject>())
                {
                    if (TaskList.Count >= MaxTasks) break;
                    TaskList.Add(new TaskItem
                    {
                        Id = (i++).ToString(),
                        Subject = (string?)t["content"] ?? "",
                        ActiveForm = (string?)t["activeForm"],
                        Status = NormalizeStatus((string?)t["status"]),
                    });
                }
                break;

            case "TaskCreate":
            {
                // The id is assigned by the tool and only appears in its RESULT; park the item until then.
                var item = new TaskItem
                {
                    Subject = (string?)input["subject"] ?? "",
                    ActiveForm = (string?)input["activeForm"] ?? (string?)input["active_form"],
                    Status = "pending",
                };
                if (item.Subject.Length > 0) PendingCreates[toolUseId] = item;
                break;
            }

            case "TaskUpdate":
            {
                // The model sometimes emits close-but-wrong key names; the CLI repairs them before
                // execution, but the transcript carries what the model wrote.
                var id = (string?)input["taskId"] ?? (string?)input["id"] ?? (string?)input["task_id"];
                if (string.IsNullOrEmpty(id)) return;
                var task = TaskList.FirstOrDefault(t => t.Id == id);
                var status = (string?)input["status"];
                if (string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase))
                {
                    if (task != null) TaskList.Remove(task);
                    return;
                }
                if (task == null) return; // an update to a task we never saw created
                if (!string.IsNullOrEmpty(status)) task.Status = NormalizeStatus(status);
                var active = (string?)input["activeForm"] ?? (string?)input["active_form"];
                if (!string.IsNullOrEmpty(active)) task.ActiveForm = active;
                var subject = (string?)input["subject"];
                if (!string.IsNullOrEmpty(subject)) task.Subject = subject!;
                break;
            }
        }
    }

    private static void CompleteTaskCreate(string toolUseId, JToken? toolUseResult, string? resultText)
    {
        if (!PendingCreates.TryGetValue(toolUseId, out var item)) return;
        PendingCreates.Remove(toolUseId);

        // The structured result carries {task:{id,...}}; fall back to "#<n>" in the text, else a
        // synthetic id (the item still renders, but later TaskUpdates for it won't match).
        string? id = (string?)toolUseResult?["task"]?["id"] ?? (string?)toolUseResult?["id"];
        if (string.IsNullOrEmpty(id) && resultText != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(resultText, @"#(\d+)");
            if (m.Success) id = m.Groups[1].Value;
        }
        item.Id = string.IsNullOrEmpty(id) ? "?" + toolUseId : id!;
        if (TaskList.Count >= MaxTasks) TaskList.RemoveAt(0);
        TaskList.Add(item);
    }

    private static string NormalizeStatus(string? s) => s switch
    {
        "in_progress" or "in-progress" or "inprogress" => "in_progress",
        "completed" or "complete" or "done" => "completed",
        _ => "pending",
    };

    // ---------------- presentation helpers ----------------

    /// <summary>"mcp__vs-debug__vs_build" -> "vs_build"; everything else unchanged.</summary>
    public static string ShortName(string rawName)
    {
        if (!rawName.StartsWith("mcp__", StringComparison.Ordinal)) return rawName;
        int last = rawName.LastIndexOf("__", StringComparison.Ordinal);
        return last > 4 && last + 2 < rawName.Length ? rawName.Substring(last + 2) : rawName;
    }

    private const int MaxSummary = 70;

    /// <summary>One short phrase saying what the call acts on. Not localized: these are tool names, paths and commands.</summary>
    private static string Summarize(string name, JObject? input)
    {
        if (input == null) return "";
        string? s = name switch
        {
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => FileName((string?)input["file_path"] ?? (string?)input["notebook_path"]),
            "Bash" or "PowerShell" => (string?)input["description"] ?? (string?)input["command"],
            "Grep" => (string?)input["pattern"],
            "Glob" => (string?)input["pattern"],
            "Agent" or "Task" => (string?)input["description"] ?? (string?)input["subagent_type"],
            "WebFetch" => (string?)input["url"],
            "WebSearch" => (string?)input["query"],
            "Skill" => (string?)input["skill"],
            "TodoWrite" => "updating the task list",
            "TaskCreate" => (string?)input["subject"],
            "TaskUpdate" => (string?)input["status"],
            "AskUserQuestion" => "asking you a question",
            "openDiff" => FileName((string?)input["new_file_path"]),
            _ => FirstStringArg(input),
        };
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > MaxSummary ? s.Substring(0, MaxSummary - 1) + "…" : s;
    }

    private static string? FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return System.IO.Path.GetFileName(path); } catch { return path; }
    }

    // For the vs_* tools and anything else: the first string-valued argument that reads like a target.
    private static readonly string[] PreferredArgs = { "project", "expression", "query", "symbolId", "file", "pane", "filter", "fullyQualifiedName", "name", "title", "path" };

    private static string? FirstStringArg(JObject input)
    {
        foreach (var key in PreferredArgs)
            if ((string?)input[key] is { Length: > 0 } v) return v;
        foreach (var prop in input.Properties())
            if (prop.Value.Type == JTokenType.String && (string?)prop.Value is { Length: > 0 } v) return v;
        return null;
    }
}
