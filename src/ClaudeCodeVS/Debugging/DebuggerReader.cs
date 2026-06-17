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

        // Only break mode has a meaningful stack/locals to report.
        if (mode != dbgDebugMode.dbgBreakMode)
            return Mode(mode == dbgDebugMode.dbgRunMode ? "run" : "design");

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
                    if (n++ >= MaxFrames) break;
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

        return snap;
    }

    /// <summary>
    /// Pull on demand: args + locals for a specific call-stack frame (0 = innermost/current). Lets the
    /// model walk up the stack to inspect callers without the user touching the debugger. Break-mode only.
    /// </summary>
    public static JObject ReadFrameLocals(int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;

        var result = Mode("break");
        result["frameIndex"] = frameIndex;
        try
        {
            var target = FrameAt(dbg, frameIndex);
            if (target == null) { result["error"] = $"no frame at index {frameIndex}"; return result; }
            result["function"] = SafeFunction(target);
            AddArgsLocals(result, target);
        }
        catch (Exception e) { result["error"] = e.Message; }
        return result;
    }

    /// <summary>
    /// Pull on demand: evaluate an arbitrary expression in the context of a chosen frame (0 = current).
    /// Read-only inspection (the model can probe values mid-investigation). Break-mode only. Note that
    /// expressions with side effects (method calls) DO execute - EnvDTE has no read-only eval flag.
    /// </summary>
    public static JObject Evaluate(string expression, int frameIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbg = (ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE)?.Debugger;
        if (dbg == null) return Mode("unknown");
        if (!TryBreakMode(dbg, out var notBreak)) return notBreak;
        if (string.IsNullOrWhiteSpace(expression))
            return new JObject { ["mode"] = "break", ["error"] = "empty expression" };

        StackFrame? prev = null;
        bool switched = false;
        try
        {
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
            return new JObject
            {
                ["mode"] = "break",
                ["expression"] = expression,
                ["frameIndex"] = frameIndex,
                ["isValid"] = isValid,
                ["type"] = type,
                ["value"] = Truncate(value),
            };
        }
        catch (Exception e)
        {
            return new JObject { ["mode"] = "break", ["expression"] = expression, ["error"] = e.Message };
        }
        finally
        {
            if (switched && prev != null) { try { dbg.CurrentStackFrame = prev; } catch { } }
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
                if (n++ >= MaxBreakpoints) break;
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
            if (n++ >= MaxLocals) break;

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

    private static JObject Mode(string m) => new JObject { ["mode"] = m };

    /// <summary>Attach a non-fatal note to the snapshot (a partial read is still useful to the model).</summary>
    private static void Log(JObject snap, string note)
    {
        if (snap["notes"] is not JArray notes)
            snap["notes"] = notes = new JArray();
        notes.Add(note);
    }
}
