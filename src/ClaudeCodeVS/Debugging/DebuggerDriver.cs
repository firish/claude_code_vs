using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Phase 3: DRIVES the Visual Studio debugger (continue / step / run-to-line / set+remove breakpoints).
///
/// The hard part vs. the Phase 2 readers: a control command is asynchronous - you issue "step over" and
/// then must WAIT for the debugger to stop again before the result is meaningful. We never block the UI
/// thread for that (EnvDTE's WaitForBreakOrEnd=true on the UI thread can hang the IDE). Instead we use the
/// same deferred-response shape as openDiff: subscribe to <see cref="IVsDebuggerEvents.OnModeChange"/>,
/// issue the command with WaitForBreakOrEnd=FALSE (returns immediately), park a TaskCompletionSource, and
/// let OnModeChange complete it on the next Break (or Design = program ended). Awaiting yields the UI
/// thread back to the message pump so that event can actually fire.
///
/// One command at a time (a re-entrancy guard). All EnvDTE/IVsDebugger access is on the UI thread. The
/// whole surface is gated upstream by BridgeStatus.AllowDebuggerDrive (checked in the tools).
/// </summary>
internal sealed class DebuggerDriver : IVsDebuggerEvents, IDisposable
{
    /// <summary>How long to wait for the next break before giving up and reporting "still running".</summary>
    public const int DefaultTimeoutMs = 20000; // < the shim's 60s HTTP timeout, so the POST never times out
    public const int StartTimeoutMs = 30000;   // starting a session may include a quick build before the first break

    private readonly object _gate = new();
    private IVsDebugger? _debugger;
    private uint _cookie;
    private bool _advised;
    private bool _busy;                                  // one drive command in flight at a time
    private TaskCompletionSource<DBGMODE>? _breakWaiter;  // completed by OnModeChange on the next break/end

    // ===== execution control (issue -> await next break -> return new state) =====

    public Task<JObject> ContinueAsync(int timeoutMs, CancellationToken ct) => StepAsync("continue", timeoutMs, ct);
    public Task<JObject> StepOverAsync(int timeoutMs, CancellationToken ct) => StepAsync("step_over", timeoutMs, ct);
    public Task<JObject> StepIntoAsync(int timeoutMs, CancellationToken ct) => StepAsync("step_into", timeoutMs, ct);
    public Task<JObject> StepOutAsync(int timeoutMs, CancellationToken ct) => StepAsync("step_out", timeoutMs, ct);

    private async Task<JObject> StepAsync(string action, int timeoutMs, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");
        if (!IsBreak(dbg)) return NotPaused(dbg);

        if (!BeginCommand()) return Err("a debugger drive command is already in progress");
        try
        {
            EnsureAdvised();
            var waiter = ArmWaiter();          // park BEFORE issuing, so we can't miss an instant break
            IssueStep(dbg, action);
            return await AwaitBreakAsync(waiter, timeoutMs, ct);
        }
        finally { EndCommand(); }
    }

    /// <summary>Run to a file:line by setting a temporary breakpoint, continuing, then removing it.</summary>
    public async Task<JObject> RunToLineAsync(string file, int line, int timeoutMs, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");
        if (string.IsNullOrWhiteSpace(file) || line <= 0) return Err("file and a positive line are required");
        if (!IsBreak(dbg)) return NotPaused(dbg);

        if (!BeginCommand()) return Err("a debugger drive command is already in progress");
        Breakpoints? temp = null;
        try
        {
            EnsureAdvised();
            try { temp = dbg.Breakpoints.Add(File: file, Line: line); }
            catch (Exception e) { return Err($"couldn't set target breakpoint: {e.Message}"); }

            var waiter = ArmWaiter();
            dbg.Go(false);
            return await AwaitBreakAsync(waiter, timeoutMs, ct);
        }
        finally
        {
            // Best-effort one-shot: drop the temp breakpoint whether or not it's where we stopped.
            if (temp != null) { try { foreach (Breakpoint b in temp) b.Delete(); } catch { } }
            EndCommand();
        }
    }

    /// <summary>
    /// Break All: pause a RUNNING debuggee that isn't sitting on a breakpoint - the only way to inspect a
    /// hung or deadlocked program (a deadlocked thread never hits a breakpoint). Issues EnvDTE's Break with
    /// WaitForBreakOrEnd=false (never blocks the UI thread) and awaits the next break through the same
    /// OnModeChange engine as continue/step. Returns the current snapshot if already paused; errors in
    /// design mode (nothing running to pause).
    /// </summary>
    public async Task<JObject> BreakAllAsync(int timeoutMs, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");

        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; } catch { mode = dbgDebugMode.dbgDesignMode; }
        if (mode == dbgDebugMode.dbgBreakMode) return DebuggerReader.ReadSnapshot(); // already paused
        if (mode == dbgDebugMode.dbgDesignMode)
            return new JObject { ["mode"] = "design", ["error"] = "not debugging; nothing to pause (start a session with vs_start_debugging or vs_attach first)" };

        if (!BeginCommand()) return Err("a debugger drive command is already in progress");
        try
        {
            EnsureAdvised();
            var waiter = ArmWaiter();          // park BEFORE issuing so we can't miss an instant break
            try { dbg.Break(false); }          // Break All; WaitForBreakOrEnd=false (don't block the UI thread)
            catch (Exception e) { ClearWaiter(waiter); return Err($"break failed: {e.Message}"); }
            return await AwaitBreakAsync(waiter, timeoutMs, ct);
        }
        finally { EndCommand(); }
    }

    // ===== session control (start = F5 + await first break; stop = Shift+F5) =====

    /// <summary>Start a debug session (only from design mode) and run to the first break (or completion).</summary>
    public async Task<JObject> StartDebuggingAsync(int timeoutMs, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dte = Dte();
        if (dte?.Debugger == null) return Err("no debugger available");
        var dbg = dte.Debugger;

        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; } catch { mode = dbgDebugMode.dbgDesignMode; }
        if (mode != dbgDebugMode.dbgDesignMode)
            return new JObject { ["mode"] = mode == dbgDebugMode.dbgBreakMode ? "break" : "run", ["error"] = "already debugging; use vs_continue / vs_step_* or vs_stop_debugging" };

        if (!BeginCommand()) return Err("a debugger drive command is already in progress");
        try
        {
            EnsureAdvised();
            var waiter = ArmWaiter();
            // Debug.Start = F5 on the solution's startup project; returns once launched (the break comes later).
            try { dte.ExecuteCommand("Debug.Start"); }
            catch (Exception e) { ClearWaiter(waiter); return Err($"couldn't start debugging: {e.Message} (is a startup project set?)"); }
            return await AwaitBreakAsync(waiter, timeoutMs, ct);
        }
        finally { EndCommand(); }
    }

    /// <summary>Stop the running debug session (returns to design mode). No-op if not debugging.</summary>
    public async Task<JObject> StopDebuggingAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");

        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; } catch { mode = dbgDebugMode.dbgDesignMode; }
        if (mode == dbgDebugMode.dbgDesignMode)
            return new JObject { ["mode"] = "design", ["note"] = "not currently debugging" };

        try { dbg.Stop(false); }
        catch (Exception e) { return Err($"stop failed: {e.Message}"); }
        return new JObject { ["ok"] = true, ["mode"] = "design", ["note"] = "debugging stopped" };
    }

    /// <summary>
    /// Attach the debugger to a running local process - by pid (preferred, exact) or a name substring. The
    /// way to debug REAL apps (a running web app / service / desktop app) instead of F5-launching a script.
    /// Returns the attached process + the resulting mode. No break to await (attach lands in run mode).
    /// </summary>
    public async Task<JObject> AttachAsync(int pid, string? name, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");
        if (pid <= 0 && string.IsNullOrWhiteSpace(name))
            return Err("a process id (preferred) or name is required; use vs_list_processes to find it");

        try
        {
            EnvDTE.Process? match = null;
            int matchId = 0; string matchName = "";
            foreach (EnvDTE.Process p in dbg.LocalProcesses) // qualify: System.Diagnostics.Process may be in scope
            {
                int id = 0; string pname = "";
                try { id = p.ProcessID; } catch { }
                try { pname = p.Name ?? ""; } catch { }
                bool hit = pid > 0 ? id == pid
                                   : pname.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) { match = p; matchId = id; matchName = pname; break; }
            }
            if (match == null)
                return Err(pid > 0 ? $"no local process with id {pid}" : $"no local process matching '{name}'");

            match.Attach();
            return new JObject
            {
                ["ok"] = true,
                ["attached"] = new JObject { ["id"] = matchId, ["name"] = matchName },
                ["mode"] = CurrentModeString(dbg),
            };
        }
        catch (Exception e) { return Err($"attach failed: {e.Message} (the process may need elevation, or already be under a debugger)"); }
    }

    /// <summary>Detach from all debugged processes (they keep running). No-op if not debugging.</summary>
    public async Task<JObject> DetachAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");
        try { dbg.DetachAll(); }
        catch (Exception e) { return Err($"detach failed: {e.Message}"); }
        return new JObject { ["ok"] = true, ["note"] = "detached from all processes (they keep running)" };
    }

    // ===== breakpoint mutation (synchronous; no break to await) =====

    public async Task<JObject> SetBreakpointAsync(string? file, int line, string? function, string? condition, int hitCount, string? hitCountType, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");

        bool byFunction = !string.IsNullOrWhiteSpace(function);
        if (!byFunction && (string.IsNullOrWhiteSpace(file) || line <= 0))
            return Err("provide a function name, or a file and a positive line");

        try
        {
            Breakpoints added;
            if (byFunction)
            {
                // Function breakpoint: break whenever the named method is entered, wherever it's called from
                // - no file:line needed. Ideal when you don't know (or can't open) the source location, or
                // the method is in a library. (Hit-count is file:line-only; condition is supported here.)
                added = string.IsNullOrEmpty(condition)
                    ? dbg.Breakpoints.Add(Function: function!)
                    : dbg.Breakpoints.Add(Function: function!, Condition: condition);
            }
            else if (hitCount > 0)
            {
                var hct = MapHitCountType(hitCountType);
                added = string.IsNullOrEmpty(condition)
                    ? dbg.Breakpoints.Add(File: file!, Line: line, HitCount: hitCount, HitCountType: hct)
                    : dbg.Breakpoints.Add(File: file!, Line: line, Condition: condition, HitCount: hitCount, HitCountType: hct);
            }
            else
            {
                added = string.IsNullOrEmpty(condition)
                    ? dbg.Breakpoints.Add(File: file!, Line: line)
                    : dbg.Breakpoints.Add(File: file!, Line: line, Condition: condition);
            }

            int bound = 0; try { bound = added.Count; } catch { }
            var result = new JObject { ["ok"] = true, ["bound"] = bound };
            if (byFunction) result["function"] = function;
            else { result["file"] = file; result["line"] = line; }
            if (!string.IsNullOrEmpty(condition)) result["condition"] = condition;
            if (!byFunction && hitCount > 0) { result["hitCount"] = hitCount; result["hitCountType"] = string.IsNullOrEmpty(hitCountType) ? "equal" : hitCountType; }
            return result;
        }
        catch (Exception e) { return Err($"set breakpoint failed: {e.Message}"); }
    }

    private static dbgHitCountType MapHitCountType(string? t) => (t ?? "equal").Trim().ToLowerInvariant() switch
    {
        "atleast" or "greaterorequal" or ">=" => dbgHitCountType.dbgHitCountTypeGreaterOrEqual,
        "multiple" or "everynth" => dbgHitCountType.dbgHitCountTypeMultiple,
        _ => dbgHitCountType.dbgHitCountTypeEqual,
    };

    /// <summary>Freeze (suspend) or thaw a thread by id - isolate one thread in a race. Break-mode only.</summary>
    public async Task<JObject> FreezeThreadAsync(int threadId, bool freeze, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");
        if (!IsBreak(dbg)) return NotPaused(dbg);

        try
        {
            var program = dbg.CurrentProgram;
            if (program == null) return Err("no running program");
            foreach (EnvDTE.Thread th in program.Threads) // qualify: System.Threading.Thread is also in scope
            {
                int id = -1; try { id = th.ID; } catch { }
                if (id != threadId) continue;
                if (freeze) th.Freeze(); else th.Thaw();
                return new JObject { ["ok"] = true, ["threadId"] = threadId, ["frozen"] = freeze };
            }
            return Err($"no thread with id {threadId}");
        }
        catch (Exception e) { return Err($"freeze/thaw failed: {e.Message}"); }
    }

    /// <summary>
    /// Move the execution pointer to file:line without running the intervening code. No direct API - we
    /// position the editor caret then issue the Debug.SetNextStatement command (which acts on the caret).
    /// Only valid within the current method; stays paused (no await needed). Break-mode only.
    /// </summary>
    public async Task<JObject> SetNextStatementAsync(string file, int line, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dte = Dte();
        if (dte?.Debugger == null) return Err("no debugger available");
        if (!IsBreak(dte.Debugger)) return NotPaused(dte.Debugger);
        if (string.IsNullOrWhiteSpace(file) || line <= 0) return Err("file and a positive line are required");

        try
        {
            var window = dte.ItemOperations.OpenFile(file);
            window?.Activate();
            if (dte.ActiveDocument?.Selection is TextSelection sel)
                sel.MoveToLineAndOffset(line, 1, false);
            dte.ExecuteCommand("Debug.SetNextStatement");
            return DebuggerReader.ReadSnapshot();
        }
        catch (Exception e)
        {
            return Err($"set next statement failed: {e.Message} (only valid within the current method)");
        }
    }

    public async Task<JObject> RemoveBreakpointAsync(string file, int line, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dbg = Dte()?.Debugger;
        if (dbg == null) return Err("no debugger available");

        int removed = 0;
        try
        {
            // Collect first - Delete() mutates the collection we'd be iterating.
            var matches = new List<Breakpoint>();
            foreach (Breakpoint bp in dbg.Breakpoints)
            {
                string f = ""; int ln = 0;
                try { f = bp.File ?? ""; } catch { }
                try { ln = bp.FileLine; } catch { }
                if (ln == line && PathEquals(f, file)) matches.Add(bp);
            }
            foreach (var bp in matches) { try { bp.Delete(); removed++; } catch { } }
        }
        catch (Exception e) { return Err($"remove breakpoint failed: {e.Message}"); }

        return new JObject { ["ok"] = true, ["file"] = file, ["line"] = line, ["removed"] = removed };
    }

    /// <summary>
    /// Break-on-thrown (first-chance): stop at the THROW SITE of a named managed exception, not where it's
    /// caught. Surfaces bugs a generic catch swallows - the exception originates deep and you only see a
    /// vague "skipped" downstream. Uses the EnvDTE90 exception-settings API (<c>Debugger3</c>) - the proven
    /// managed path, NOT the low-level AD7 IDebugEngine2 route. Works in any mode; when the exception later
    /// fires, the break arrives through the same OnModeChange path as a breakpoint.
    /// </summary>
    public async Task<JObject> SetBreakOnThrownAsync(string exceptionName, bool enabled, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dte = Dte();
        if (dte?.Debugger == null) return Err("no debugger available");
        if (string.IsNullOrWhiteSpace(exceptionName))
            return Err("an exception type name is required, e.g. 'System.NullReferenceException'");

        // ExceptionGroups lives on Debugger3 (EnvDTE90), not the base EnvDTE.Debugger - cast up. This is
        // exactly the cast the prior break-on-thrown attempt missed (it used EnvDTE.Debugger and hit CS1061).
        if (dte.Debugger is not EnvDTE90.Debugger3 d3)
            return Err("this VS doesn't expose exception settings (EnvDTE90.Debugger3 unavailable)");

        // The CLR/.NET exception category. Null when no solution/project is loaded (mirrors the
        // "Error List empty for loose files" caveat) - report that rather than NRE-ing.
        EnvDTE90.ExceptionSettings? clr;
        try { clr = d3.ExceptionGroups?.Item("Common Language Runtime Exceptions"); }
        catch (Exception e) { return Err($"CLR exception settings unavailable: {e.Message}"); }
        if (clr == null) return Err("CLR exception settings unavailable - open a solution/project first");

        try
        {
            // Touch a SINGLE named type: look it up, and if it isn't already listed, register it. We never
            // enumerate the whole category - setting thousands of children is a multi-minute UI freeze.
            EnvDTE90.ExceptionSetting? ex = null;
            try { ex = clr.Item(exceptionName); } catch { /* not in the list yet */ }
            if (ex == null) ex = clr.NewException(exceptionName, 0); // code 0: managed exceptions key on name

            clr.SetBreakWhenThrown(enabled, ex);
            return new JObject
            {
                ["ok"] = true,
                ["exception"] = exceptionName,
                ["breakWhenThrown"] = enabled,
                ["note"] = enabled
                    ? "VS will now break at the throw site of this exception (first-chance), even if it's caught."
                    : "Break-on-thrown cleared for this exception.",
            };
        }
        catch (Exception e) { return Err($"set break-on-thrown failed: {e.Message}"); }
    }

    // ===== the await-break engine =====

    /// <summary>
    /// Await the parked waiter (completed by OnModeChange) or a timeout. We're on the UI thread; the await
    /// yields it to the pump so OnModeChange can fire. Returns the fresh snapshot on break, a design note
    /// on program end, or a "still running" note on timeout.
    /// </summary>
    private async Task<JObject> AwaitBreakAsync(TaskCompletionSource<DBGMODE> waiter, int timeoutMs, CancellationToken ct)
    {
        // VSTHRD003: we await a parked TCS that's completed from OnModeChange. This is the same deferred
        // pattern as openDiff and is deadlock-free here: we AWAIT (never block) the UI thread, so the
        // message pump stays free for OnModeChange to fire, and the TCS uses RunContinuationsAsynchronously
        // so completion never re-enters VS inline. Suppress the (unprovable-to-the-analyzer) deadlock warning.
#pragma warning disable VSTHRD003
        var completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, ct));
        if (completed != waiter.Task)
        {
            ClearWaiter(waiter);
            return Running(ct.IsCancellationRequested
                ? "cancelled while waiting for the next break"
                : $"no break hit within {timeoutMs / 1000}s; program is still running");
        }

        var mode = await waiter.Task;
#pragma warning restore VSTHRD003
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct); // read state on the UI thread
        if (mode == DBGMODE.DBGMODE_Design)
            return new JObject { ["mode"] = "design", ["note"] = "program ended or debugging stopped" };
        return DebuggerReader.ReadSnapshot();
    }

    /// <summary>VS calls this on every debugger mode transition (UI thread). Complete a parked waiter on break/end.</summary>
    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        if (dbgmodeNew == DBGMODE.DBGMODE_Break || dbgmodeNew == DBGMODE.DBGMODE_Design)
        {
            TaskCompletionSource<DBGMODE>? w;
            lock (_gate) { w = _breakWaiter; _breakWaiter = null; }
            w?.TrySetResult(dbgmodeNew); // RunContinuationsAsynchronously -> no re-entrancy into VS
        }
        return VSConstants.S_OK;
    }

    private void EnsureAdvised()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_advised) return;
        _debugger = ServiceProvider.GlobalProvider.GetService(typeof(SVsShellDebugger)) as IVsDebugger;
        if (_debugger != null && _debugger.AdviseDebuggerEvents(this, out _cookie) == VSConstants.S_OK)
            _advised = true;
    }

    private TaskCompletionSource<DBGMODE> ArmWaiter()
    {
        var tcs = new TaskCompletionSource<DBGMODE>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _breakWaiter = tcs; }
        return tcs;
    }

    private void ClearWaiter(TaskCompletionSource<DBGMODE> tcs)
    {
        lock (_gate) { if (ReferenceEquals(_breakWaiter, tcs)) _breakWaiter = null; }
    }

    private bool BeginCommand() { lock (_gate) { if (_busy) return false; _busy = true; return true; } }
    private void EndCommand() { lock (_gate) { _busy = false; } }

    private static void IssueStep(Debugger dbg, string action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        switch (action)
        {
            case "continue": dbg.Go(false); break;
            case "step_over": dbg.StepOver(false); break;
            case "step_into": dbg.StepInto(false); break;
            case "step_out": dbg.StepOut(false); break;
        }
    }

    private static DTE? Dte()
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // every caller switches to the UI thread first
        return ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE;
    }

    private static bool IsBreak(Debugger dbg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return dbg.CurrentMode == dbgDebugMode.dbgBreakMode; } catch { return false; }
    }

    private static string CurrentModeString(Debugger dbg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return dbg.CurrentMode switch
            {
                dbgDebugMode.dbgBreakMode => "break",
                dbgDebugMode.dbgRunMode => "run",
                _ => "design",
            };
        }
        catch { return "unknown"; }
    }

    private static JObject NotPaused(Debugger dbg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string m;
        try { m = dbg.CurrentMode == dbgDebugMode.dbgRunMode ? "run" : "design"; } catch { m = "unknown"; }
        return new JObject { ["mode"] = m, ["error"] = "not paused at a breakpoint; nothing to drive" };
    }

    private static JObject Err(string msg) => new JObject { ["error"] = msg };
    private static JObject Running(string note) => new JObject { ["mode"] = "run", ["note"] = note };

    private static bool PathEquals(string a, string b) => string.Equals(
        a.Replace('/', '\\').TrimEnd('\\'),
        b.Replace('/', '\\').TrimEnd('\\'),
        StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        // Best-effort unadvise. On shutdown the UI thread may be gone; UnadviseDebuggerEvents can throw
        // off-thread, so swallow - the process is tearing down anyway.
        try
        {
            if (_advised && _debugger != null)
            {
#pragma warning disable VSTHRD010
                _debugger.UnadviseDebuggerEvents(_cookie);
#pragma warning restore VSTHRD010
                _advised = false;
            }
        }
        catch { }
    }
}
