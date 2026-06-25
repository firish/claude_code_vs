using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Reads the live Visual Studio debugger state (break location, call stack, locals/arguments) via the
/// EnvDTE automation model and shapes it as JSON for the UserPromptSubmit hook to inject into Claude's
/// context. This is the debug analog of <see cref="ClaudeCodeVs.Editor.ErrorListReader"/>: VS already
/// has all of this; we just expose it so the model can reason about runtime values, not just source.
///
/// EnvDTE is UI-thread bound and throws readily while the debugger transitions, so every access is
/// individually guarded and the whole read runs on the UI thread. When not stopped at a breakpoint we
/// return just {"mode": ...} so the hook injects nothing (no noise on non-debugging turns).
/// </summary>
internal static class DebuggerReader
{
    private const int MaxFrames = 20;        // cap call-stack depth sent to the model
    private const int MaxLocals = 60;        // cap variables per frame
    private const int MaxValueLen = 240;     // truncate long value renderings (deep object graphs)
    private const int MaxBreakpoints = 200;  // cap breakpoints listed (a single source bp can bind to many)
    private const int EvalTimeoutMs = 5000;  // expression-evaluation timeout (GetExpression)
    private const int MaxLogValues = 10;     // cap name=value pairs in a one-line log summary
    private const int MaxLogValueLen = 40;   // truncate each value in that log summary
    private const int MaxExpandDepth = 3;     // cap recursion when expanding an object graph
    private const int MaxExpandChildren = 40; // cap child members rendered per level
    private const int MaxThreads = 60;        // cap threads listed
    private const int MaxThreadFrames = 12;   // cap call-stack depth reported per thread
    private const int MaxProcesses = 200;     // cap processes listed (the machine has hundreds; filter by name)

    /// <summary>Read a debug-state snapshot. Must be called on the UI thread.</summary>
    public static JObject ReadSnapshot()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE;
        if (dte == null) return Mode("unknown");
        var dbg = dte.Debugger;
        if (dbg == null) return Mode("unknown");

        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; }
        catch { return Mode("unknown"); }

        // Design mode = not debugging; nothing to report. Run mode = debugging but not paused: report the
        // session shape (what we're attached to) but no stack. Break mode = the full snapshot below.
        if (mode == dbgDebugMode.dbgDesignMode)
            return Mode("design");
        if (mode != dbgDebugMode.dbgBreakMode)
        {
            var runSnap = Mode("run");
            AddDebuggedProcesses(runSnap, dbg);
            return runSnap;
        }

        var snap = Mode("break");

        // Where we're stopped. Per-frame source isn't on EnvDTE.StackFrame; for the *current* stop we
        // use the broken-into active document + caret line (VS positions it on the stop line). Precise
        // per-frame file/line via IDebugStackFrame2 is a fast follow (see ROADMAP/CLAUDE notes).
        try
        {
            var doc = dte.ActiveDocument;
            if (doc != null)
            {
                snap["stoppedAt"] = new JObject
                {
                    ["file"] = doc.FullName,
                    ["line"] = CurrentLine(doc),
                    ["function"] = SafeFunction(dbg.CurrentStackFrame),
                };
            }
        }
        catch (Exception e) { Log(snap, $"stoppedAt unavailable: {e.Message}"); }

        // Call stack (innermost first) - function names only for now.
        try
        {
            var frames = new JArray();
            var thread = dbg.CurrentThread;
            if (thread != null)
            {
                int n = 0;
                foreach (StackFrame f in thread.StackFrames)
                {
                    if (n >= MaxFrames) { frames.Add(TruncMarker($"capped at {MaxFrames} frames; deeper frames omitted")); break; }
                    n++;
                    frames.Add(new JObject { ["function"] = SafeFunction(f) });
                }
            }
            snap["callStack"] = frames;
        }
        catch (Exception e) { Log(snap, $"callStack unavailable: {e.Message}"); }

        // Arguments + locals for the current frame.
        try
        {
            var frame = dbg.CurrentStackFrame;
            if (frame != null) AddArgsLocals(snap, frame);
        }
        catch (Exception e) { Log(snap, $"locals unavailable: {e.Message}"); }

        AddDebuggedProcesses(snap, dbg); // session shape: what we're attached to (multi-process awareness)
        return snap;
    }

    /// <summary>
    /// Pull on demand: args + locals for a specific call-stack frame (0 = innermost/current). Lets the
    /// model walk up the stack to inspect callers without the user touching the debugger. With a non-zero
    /// <paramref name="threadId"/> (from vs_threads) it reads that thread's frame instead of the current
    /// one - e.g. inspecting each thread parked in a deadlock. Break-mode only.
    /// </summary>
    public static JObject ReadFrameLocals(int frameIndex, int threadId = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;

        var result = Mode("break");
        result["frameIndex"] = frameIndex;

        // To read a NON-current thread's locals, make it the current thread first, then restore. EnvDTE
        // evaluates in the current context, so reading another thread's StackFrame.Locals directly tends to
        // come back empty/unavailable - switching is the reliable path. Restore afterward so the user's
        // debugger UI is undisturbed (same idea as the CurrentStackFrame switch in Evaluate/Expand).
        EnvDTE.Thread? prevThread = null;
        bool switched = false;
        try
        {
            if (threadId > 0)
            {
                var targetThread = FindThread(dbg, threadId);
                if (targetThread == null) { result["error"] = $"no thread with id {threadId}"; return result; }
                try { prevThread = dbg.CurrentThread; } catch { }
                dbg.CurrentThread = targetThread;
                switched = true;
                result["threadId"] = threadId;
            }

            var target = FrameAt(dbg, frameIndex);
            if (target == null)
            {
                result["error"] = threadId > 0 ? $"no frame at index {frameIndex} on thread {threadId}" : $"no frame at index {frameIndex}";
                return result;
            }
            result["function"] = SafeFunction(target);
            AddArgsLocals(result, target);
        }
        catch (Exception e) { result["error"] = e.Message; }
        finally
        {
            if (switched && prevThread != null) { try { dbg.CurrentThread = prevThread; } catch { } }
        }
        return result;
    }

    /// <summary>
    /// Pull on demand: evaluate an arbitrary expression in the context of a chosen frame (0 = current).
    /// Read-only inspection (the model can probe values mid-investigation). Break-mode only. Note that
    /// expressions with side effects (method calls) DO execute - EnvDTE has no read-only eval flag.
    /// </summary>
    public static JObject Evaluate(string expression, int frameIndex, int threadId = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;
        if (string.IsNullOrWhiteSpace(expression))
            return new JObject { ["mode"] = "break", ["error"] = "empty expression" };

        EnvDTE.Thread? prevThread = null; bool threadSwitched = false;
        StackFrame? prev = null;
        bool switched = false;
        try
        {
            // To evaluate in ANOTHER thread's context (e.g. read `from.Id` on each thread in a deadlock),
            // make it current first - GetExpression and FrameAt both work off the current thread. Restored in
            // finally, same switch-and-restore as ReadFrameLocals.
            if (threadId > 0)
            {
                var targetThread = FindThread(dbg, threadId);
                if (targetThread == null)
                    return new JObject { ["mode"] = "break", ["expression"] = expression, ["error"] = $"no thread with id {threadId}" };
                try { prevThread = dbg.CurrentThread; } catch { }
                dbg.CurrentThread = targetThread;
                threadSwitched = true;
            }

            // GetExpression evaluates relative to dbg.CurrentStackFrame, so temporarily retarget it when
            // a non-current frame is requested, then restore so the user's debugger UI is undisturbed.
            if (frameIndex > 0)
            {
                var target = FrameAt(dbg, frameIndex);
                if (target != null)
                {
                    prev = dbg.CurrentStackFrame;
                    dbg.CurrentStackFrame = target;
                    switched = true;
                }
            }

            var ex = dbg.GetExpression(expression, true, EvalTimeoutMs);
            // Read each member into a local with its own guard (EnvDTE throws readily on invalid exprs).
            bool isValid = false; string type = "", value = "";
            try { isValid = ex.IsValidValue; } catch { }
            try { type = ex.Type ?? ""; } catch { }
            try { value = ex.Value ?? ""; } catch { }
            var result = new JObject
            {
                ["mode"] = "break",
                ["expression"] = expression,
                ["frameIndex"] = frameIndex,
                ["isValid"] = isValid,
                ["type"] = type,
                ["value"] = Truncate(value),
            };
            if (threadId > 0) result["threadId"] = threadId;
            return result;
        }
        catch (Exception e)
        {
            return new JObject { ["mode"] = "break", ["expression"] = expression, ["error"] = e.Message };
        }
        finally
        {
            if (switched && prev != null) { try { dbg.CurrentStackFrame = prev; } catch { } }
            if (threadSwitched && prevThread != null) { try { dbg.CurrentThread = prevThread; } catch { } }
        }
    }

    /// <summary>
    /// List all breakpoints (file/line/function/enabled/hit-count/condition). Unlike the others this works
    /// in ANY mode - breakpoints exist before a run, so the model can see where execution will stop.
    /// </summary>
    public static JObject ReadBreakpoints()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var result = new JObject { ["mode"] = "unknown", ["breakpoints"] = new JArray() };
        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return result;
        result["mode"] = ModeString(dbg);

        try
        {
            var arr = (JArray)result["breakpoints"]!;
            var col = dbg.Breakpoints;
            if (col == null) return result;
            int n = 0;
            foreach (Breakpoint bp in col)
            {
                if (n >= MaxBreakpoints) { arr.Add(TruncMarker($"capped at {MaxBreakpoints} breakpoints")); break; }
                n++;
                // Per-property guards: a file breakpoint throws on FunctionName and vice-versa.
                string file = "", function = "", condition = "";
                int line = 0, hits = 0; bool enabled = false;
                try { file = bp.File ?? ""; } catch { }
                try { line = bp.FileLine; } catch { }
                try { function = bp.FunctionName ?? ""; } catch { }
                try { enabled = bp.Enabled; } catch { }
                try { hits = bp.CurrentHits; } catch { }
                try { condition = bp.Condition ?? ""; } catch { }

                var o = new JObject
                {
                    ["file"] = file,
                    ["line"] = line,
                    ["function"] = function,
                    ["enabled"] = enabled,
                    ["hitCount"] = hits,
                };
                if (!string.IsNullOrEmpty(condition)) o["condition"] = condition;
                arr.Add(o);
            }
        }
        catch (Exception e) { result["error"] = e.Message; }
        return result;
    }

    /// <summary>
    /// Expand an expression's object graph: evaluate it, then recurse into its child members
    /// (<see cref="Expression.DataMembers"/>) down to <paramref name="depth"/> levels. Lets the model
    /// drill into a complex object without guessing every member path. Break-mode only; depth/breadth capped.
    /// </summary>
    public static JObject Expand(string expression, int frameIndex, int depth, int threadId = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;
        if (string.IsNullOrWhiteSpace(expression))
            return new JObject { ["mode"] = "break", ["error"] = "empty expression" };

        EnvDTE.Thread? prevThread = null; bool threadSwitched = false;
        StackFrame? prev = null;
        bool switched = false;
        try
        {
            // Expand in ANOTHER thread's context (e.g. drill `from` on each thread in a deadlock): make it
            // current first, restore in finally (same switch-and-restore as ReadFrameLocals / Evaluate).
            if (threadId > 0)
            {
                var targetThread = FindThread(dbg, threadId);
                if (targetThread == null)
                    return new JObject { ["mode"] = "break", ["expression"] = expression, ["error"] = $"no thread with id {threadId}" };
                try { prevThread = dbg.CurrentThread; } catch { }
                dbg.CurrentThread = targetThread;
                threadSwitched = true;
            }

            if (frameIndex > 0)
            {
                var target = FrameAt(dbg, frameIndex);
                if (target != null) { prev = dbg.CurrentStackFrame; dbg.CurrentStackFrame = target; switched = true; }
            }

            var ex = dbg.GetExpression(expression, true, EvalTimeoutMs);
            var node = ExpandExpression(ex, Math.Min(Math.Max(depth, 0), MaxExpandDepth));
            node["mode"] = "break";
            node["expression"] = expression;
            node["frameIndex"] = frameIndex;
            if (threadId > 0) node["threadId"] = threadId;
            return node;
        }
        catch (Exception e)
        {
            return new JObject { ["mode"] = "break", ["expression"] = expression, ["error"] = e.Message };
        }
        finally
        {
            if (switched && prev != null) { try { dbg.CurrentStackFrame = prev; } catch { } }
            if (threadSwitched && prevThread != null) { try { dbg.CurrentThread = prevThread; } catch { } }
        }
    }

    private static JObject ExpandExpression(Expression ex, int depth)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var node = new JObject();
        string name = "", type = "", value = "";
        try { name = ex.Name ?? ""; } catch { }
        try { type = ex.Type ?? ""; } catch { }
        try { value = ex.Value ?? ""; } catch { }
        node["name"] = name;
        node["type"] = type;
        node["value"] = Truncate(value);

        if (depth <= 0) return node;

        Expressions? members = null;
        try { members = ex.DataMembers; } catch { }
        int count = 0;
        try { count = members?.Count ?? 0; } catch { }
        if (members == null || count == 0) return node;

        var kids = new JArray();
        int n = 0;
        foreach (Expression child in members)
        {
            if (n++ >= MaxExpandChildren)
            {
                kids.Add(new JObject { ["name"] = "…", ["truncated"] = true, ["value"] = $"{count - MaxExpandChildren} more members not shown" });
                break;
            }
            kids.Add(ExpandExpression(child, depth - 1));
        }
        node["children"] = kids;
        return node;
    }

    /// <summary>
    /// List ALL threads of the debuggee (not just the current one), each with its call stack (function
    /// names) and location, plus a wait/lock flag. The tool for deadlocks/races. Break-mode only. For a
    /// CONTENDED lock the Concord engine annotates the stack with the owning thread, which we surface as
    /// lockOwnerThreadId - so a deadlock cycle is followable thread-to-thread (id -> lockOwnerThreadId).
    /// </summary>
    public static JObject ReadThreads()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        var result = new JObject { ["mode"] = "unknown", ["threads"] = new JArray() };
        if (dbg == null) return result;
        result["mode"] = ModeString(dbg);

        try
        {
            var program = dbg.CurrentProgram;
            if (program == null) return result;

            int current = -1;
            try { current = dbg.CurrentThread?.ID ?? -1; } catch { }

            var arr = (JArray)result["threads"]!;
            int tn = 0;
            foreach (EnvDTE.Thread th in program.Threads)
            {
                if (tn >= MaxThreads) { arr.Add(TruncMarker($"capped at {MaxThreads} threads")); break; }
                tn++;
                var o = new JObject();
                int id = -1;
                try { id = th.ID; } catch { }
                o["id"] = id;
                if (id == current) o["current"] = true;
                try { o["name"] = th.Name ?? ""; } catch { }
                try { o["location"] = th.Location ?? ""; } catch { }

                var frames = new JArray();
                try
                {
                    int fn = 0;
                    foreach (StackFrame f in th.StackFrames)
                    {
                        if (fn >= MaxThreadFrames) { frames.Add($"… capped at {MaxThreadFrames} frames"); break; }
                        fn++;
                        frames.Add(SafeFunction(f));
                    }
                }
                catch { }
                o["stack"] = frames;
                // Deadlock/contention triage: flag threads parked on a lock/wait. A CONTENDED lock shows up as
                // Concord's synthetic "[Waiting on lock owned by Thread 0x..]" annotation frame (NOT a
                // Monitor.Enter BCL frame), which also names the owner - we match it and surface that owner so a
                // deadlock cycle is readable straight from the flags. Other waits match the BCL markers.
                ApplyWaitFlags(o, frames);
                arr.Add(o);
            }
        }
        catch (Exception e) { result["error"] = e.Message; }
        return result;
    }

    /// <summary>
    /// Inspect the exception in scope ($exception) while paused - at a first-chance break (after
    /// vs_break_on_thrown reaches a throw) or inside a catch block. Returns its type, message, and an
    /// expanded tree (incl. InnerException + stack) so the model sees WHAT was thrown and WHY without
    /// having to know the $exception pseudo-variable. Break-mode only.
    /// </summary>
    public static JObject ReadException(int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;

        StackFrame? prev = null;
        bool switched = false;
        try
        {
            if (frameIndex > 0)
            {
                var target = FrameAt(dbg, frameIndex);
                if (target != null) { prev = dbg.CurrentStackFrame; dbg.CurrentStackFrame = target; switched = true; }
            }

            var ex = dbg.GetExpression("$exception", true, EvalTimeoutMs);
            bool valid = false; try { valid = ex.IsValidValue; } catch { }
            if (!valid)
                return new JObject
                {
                    ["mode"] = "break",
                    ["exception"] = null,
                    ["note"] = "no exception in scope here. Reach a throw site (vs_break_on_thrown, then run to it) "
                             + "or stop inside a catch block, then retry.",
                };

            var result = new JObject { ["mode"] = "break", ["frameIndex"] = frameIndex };
            try { result["type"] = ex.Type ?? ""; } catch { }
            // Message is the single most useful field - pull it out explicitly alongside the full tree.
            try
            {
                var msg = dbg.GetExpression("$exception.Message", true, EvalTimeoutMs);
                if (msg.IsValidValue) result["message"] = Truncate(msg.Value ?? "");
            }
            catch { }
            result["object"] = ExpandExpression(ex, 2); // depth 2: surfaces InnerException + key fields
            return result;
        }
        catch (Exception e) { return new JObject { ["mode"] = "break", ["error"] = e.Message }; }
        finally { if (switched && prev != null) { try { dbg.CurrentStackFrame = prev; } catch { } } }
    }

    /// <summary>
    /// List local processes available to attach to (optionally filtered by a name substring), each with
    /// id + name, flagged if we're already debugging it. The key to debugging REAL apps - a running web app
    /// (Kestrel / IIS w3wp), service, or desktop app - instead of only F5-launching a startup project.
    /// Works in any mode. The machine has hundreds of processes, so pass a filter (e.g. 'dotnet', 'w3wp').
    /// </summary>
    public static JObject ReadProcesses(string? filter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var result = new JObject { ["mode"] = "unknown", ["processes"] = new JArray() };
        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return result;
        result["mode"] = ModeString(dbg);
        if (!string.IsNullOrEmpty(filter)) result["filter"] = filter;

        try
        {
            // What we're already attached to, so the list can flag it.
            var attached = new HashSet<int>();
            try { foreach (EnvDTE.Process dp in dbg.DebuggedProcesses) { try { attached.Add(dp.ProcessID); } catch { } } }
            catch { }

            var arr = (JArray)result["processes"]!;
            int n = 0, matched = 0;
            foreach (EnvDTE.Process p in dbg.LocalProcesses) // qualify: System.Diagnostics.Process could be in scope
            {
                string name = ""; int id = 0;
                try { name = p.Name ?? ""; } catch { }
                try { id = p.ProcessID; } catch { }
                if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                matched++;
                if (n >= MaxProcesses) { arr.Add(TruncMarker($"capped at {MaxProcesses} processes; narrow with a name filter")); break; }
                n++;
                var o = new JObject { ["id"] = id, ["name"] = name };
                if (attached.Contains(id)) o["attached"] = true;
                arr.Add(o);
            }
            result["count"] = matched;
        }
        catch (Exception e) { result["error"] = e.Message; }
        return result;
    }

    /// <summary>Read a frame's args + locals into <paramref name="into"/>, deduping params out of locals.</summary>
    private static void AddArgsLocals(JObject into, StackFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // EnvDTE's Locals collection ALSO includes the method parameters, so read args first and exclude
        // their names from locals to avoid duplicates.
        var args = ReadExpressions(frame.Arguments);
        into["args"] = args;

        var argNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in args)
            argNames.Add((string?)a["name"] ?? "");

        into["locals"] = ReadExpressions(frame.Locals, argNames);
    }

    /// <summary>
    /// Add the set of processes currently being debugged ("session shape") - so after a vs_attach, or a
    /// multi-process session (client+server, a web worker pool), the model knows what it's attached to and
    /// can pick a process to reason about. Only called while actually debugging (run/break). Best-effort:
    /// never let session-shape info break the snapshot.
    /// </summary>
    private static void AddDebuggedProcesses(JObject snap, Debugger dbg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var procs = dbg.DebuggedProcesses;
            if (procs == null) return;
            var arr = new JArray();
            int n = 0;
            foreach (EnvDTE.Process p in procs) // qualify: System.Diagnostics.Process could be in scope
            {
                if (n++ >= MaxProcesses) { arr.Add(TruncMarker($"capped at {MaxProcesses} processes")); break; }
                int id = 0; string name = "";
                try { id = p.ProcessID; } catch { }
                try { name = p.Name ?? ""; } catch { }
                arr.Add(new JObject { ["id"] = id, ["name"] = name });
            }
            if (arr.Count > 0) snap["debuggedProcesses"] = arr;
        }
        catch { /* best-effort */ }
    }

    /// <summary>The EnvDTE.Thread with <paramref name="threadId"/> in the current program, or null.</summary>
    private static EnvDTE.Thread? FindThread(Debugger dbg, int threadId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EnvDTE.Program? program;
        try { program = dbg.CurrentProgram; } catch { return null; }
        if (program == null) return null;
        foreach (EnvDTE.Thread th in program.Threads)
        {
            int id = -1;
            try { id = th.ID; } catch { }
            if (id == threadId) return th;
        }
        return null;
    }

    /// <summary>The StackFrame at <paramref name="index"/> (0 = innermost), or null if out of range.</summary>
    private static StackFrame? FrameAt(Debugger dbg, int index)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var thread = dbg.CurrentThread;
        if (thread == null || index < 0) return null;
        int n = 0;
        foreach (StackFrame f in thread.StackFrames)
            if (n++ == index) return f;
        return null;
    }

    /// <summary>True when stopped at a breakpoint. Otherwise <paramref name="notBreak"/> carries the mode payload.</summary>
    private static bool TryBreakMode(Debugger dbg, out JObject notBreak)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Debugger access is main-thread only
        notBreak = Mode("unknown");
        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; } catch { return false; }
        if (mode == dbgDebugMode.dbgBreakMode) return true;
        notBreak = Mode(mode == dbgDebugMode.dbgRunMode ? "run" : "design");
        return false;
    }

    private static string ModeString(Debugger dbg)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Debugger access is main-thread only
        try
        {
            return dbg.CurrentMode switch
            {
                dbgDebugMode.dbgBreakMode => "break",
                dbgDebugMode.dbgRunMode => "run",
                dbgDebugMode.dbgDesignMode => "design",
                _ => "unknown",
            };
        }
        catch { return "unknown"; }
    }

    private static string Truncate(string v) => v.Length > MaxValueLen ? v.Substring(0, MaxValueLen) + "…" : v;

    /// <summary>A list element marking where a cap truncated output, so the model knows data was cut (not "all of it").</summary>
    private static JObject TruncMarker(string note) => new JObject { ["truncated"] = true, ["note"] = note };

    /// <summary>
    /// Compact "name=value, …" rendering of a snapshot's args + locals, for a single log line - so the
    /// Output pane shows the runtime values the model is reasoning over, not just the stop location.
    /// Pure JObject reading (no EnvDTE), so it's safe to call off the UI thread.
    /// </summary>
    public static string SummarizeValues(JObject snap)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "args", "locals" })
        {
            if (snap[key] is not JArray arr) continue;
            foreach (var t in arr)
            {
                if (t is not JObject e) continue;
                if (e["truncated"] != null) continue; // skip truncation markers in the log summary
                var name = (string?)e["name"] ?? "";
                var val = (string?)e["value"] ?? "";
                if (val.Length > MaxLogValueLen) val = val.Substring(0, MaxLogValueLen) + "…";
                parts.Add($"{name}={val}");
                if (parts.Count >= MaxLogValues) return string.Join(", ", parts) + ", …";
            }
        }
        return string.Join(", ", parts);
    }

    private static JArray ReadExpressions(Expressions exprs, HashSet<string>? exclude = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Expression access is main-thread only
        var arr = new JArray();
        if (exprs == null) return arr;

        int n = 0;
        foreach (Expression e in exprs)
        {
            string name = "";
            try { name = e.Name ?? ""; } catch { }
            if (exclude != null && exclude.Contains(name)) continue; // drop dupes (params show up in Locals too)
            if (n >= MaxLocals) { arr.Add(TruncMarker($"capped at {MaxLocals} variables; more not shown")); break; }
            n++;

            string type = "", value = "";
            try { type = e.Type ?? ""; } catch { }
            try { value = e.Value ?? ""; } catch { }
            if (value.Length > MaxValueLen) value = value.Substring(0, MaxValueLen) + "…";

            arr.Add(new JObject { ["name"] = name, ["type"] = type, ["value"] = value });
        }
        return arr;
    }

    private static int CurrentLine(Document doc)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Document/TextSelection access is main-thread only
        try { return doc.Selection is TextSelection ts ? ts.CurrentLine : 0; }
        catch { return 0; }
    }

    private static string SafeFunction(StackFrame f)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE StackFrame access is main-thread only
        try { return f?.FunctionName ?? ""; }
        catch { return ""; }
    }

    // Top-of-stack frame names that suggest a thread is parked on a lock/wait (deadlock/contention triage).
    private static readonly string[] WaitMarkers =
    {
        "Monitor.Enter", "Monitor.TryEnter", "Monitor.Wait", ".WaitOne", "WaitHandle", "Task.Wait",
        ".GetResult", "SemaphoreSlim", "Semaphore.", "ManualResetEvent", "AutoResetEvent", "CountdownEvent",
        "ReaderWriterLock", "SpinLock", "Thread.Join", "Thread.Sleep", "Barrier.", "Mutex.",
    };

    /// <summary>
    /// Flag a thread parked on a lock/wait by string-matching its top few (already-read) frame names. A
    /// contended lock is the important case and the trickiest: the Concord engine leaves no Monitor.Enter
    /// BCL frame, it injects a synthetic "[Waiting on lock owned by Thread 0xNNN, ...]" frame above the user
    /// method, which ALSO names the lock owner. We match that first and surface the owner as
    /// <c>lockOwnerThreadId</c> (decimal, so it cross-references another thread's <c>id</c> in this same list
    /// - follow the chain and you have the deadlock cycle). Other waits match the BCL markers. Heuristic, not
    /// authoritative, but it points the model straight at the suspects.
    /// </summary>
    private static void ApplyWaitFlags(JObject o, JArray frames)
    {
        string? lockWaitFrame = null;
        bool deadlock = false;
        string? marker = null;

        int n = 0;
        foreach (var f in frames)
        {
            if (n++ >= 3) break; // only the top few frames
            if (f?.Type != JTokenType.String) continue;
            var s = (string?)f ?? "";

            if (lockWaitFrame == null && s.IndexOf("Waiting on lock", StringComparison.OrdinalIgnoreCase) >= 0)
                lockWaitFrame = s;
            if (s.IndexOf("Deadlock", StringComparison.OrdinalIgnoreCase) >= 0)
                deadlock = true;
            if (marker == null)
                foreach (var m in WaitMarkers)
                    if (s.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { marker = m.Trim('.'); break; }
        }

        // Prefer the lock-wait annotation - it's the deadlock signal AND carries the owning thread.
        if (lockWaitFrame != null)
        {
            o["waiting"] = true;
            o["waitOn"] = "lock";
            var owner = ExtractLockOwnerId(lockWaitFrame);
            if (owner != null) o["lockOwnerThreadId"] = owner.Value;
        }
        else if (deadlock)
        {
            o["waiting"] = true;
            o["waitOn"] = "deadlock";
        }
        else if (marker != null)
        {
            o["waiting"] = true;
            o["waitOn"] = marker;
        }
    }

    /// <summary>
    /// Pull the owning thread id out of Concord's "[Waiting on lock owned by Thread 0xNNN, ...]" annotation,
    /// converted to decimal so it matches the <c>id</c> of another thread in the list. Null if not present.
    /// </summary>
    private static int? ExtractLockOwnerId(string annotation)
    {
        const string key = "owned by Thread ";
        int i = annotation.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += key.Length;
        if (i + 1 < annotation.Length && annotation[i] == '0' && (annotation[i + 1] == 'x' || annotation[i + 1] == 'X'))
            i += 2; // skip a "0x"/"0X" prefix
        int j = i;
        while (j < annotation.Length && Uri.IsHexDigit(annotation[j])) j++;
        if (j == i) return null;
        return int.TryParse(annotation.Substring(i, j - i), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int id) ? id : (int?)null;
    }

    private static JObject Mode(string m) => new JObject { ["mode"] = m };

    /// <summary>Attach a non-fatal note to the snapshot (a partial read is still useful to the model).</summary>
    private static void Log(JObject snap, string note)
    {
        if (snap["notes"] is not JArray notes)
            snap["notes"] = notes = new JArray();
        notes.Add(note);
    }
}
