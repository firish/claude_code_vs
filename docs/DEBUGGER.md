# Live debugger integration

Most coding assistants see only your *source*. This extension also gives Claude your program's **runtime state** — where execution is paused, the call stack, variable values, threads — and, opt-in, lets it **drive** the debugger (continue, step, set breakpoints, break at an exception's throw site, attach to a running process, start/stop a session) to corner a bug instead of guessing from the code.

The `claude` CLI does all the agent work; the extension exposes Visual Studio's live debugger to it over the same localhost bridge that powers the diff and diagnostics features.

---

## See it in action: ComboScore

A real run against the [`demo/ComboScore`](../demo/ComboScore) fixture — a scoring routine whose bug is **invisible in the final output** (you just see a wrong total). A *fresh* `claude` session, with no idea what the bug was, drove the Visual Studio debugger to find it in about 1m 37s.

**The prompt:**

> hi, my comboscorer is partially correct. can you use the debugger and check whats tripping up?

**What it did** — read the source, loaded the `vs-debug` tools, then *drove*:

- Set a breakpoint at the top of the loop and **started the session** (`vs_start_debugging`).
- Stepped round-by-round, **watching `combo`** — and saw it *stay at 2* across the zero round at index 2 instead of resetting.
- Ran to the `return`, confirming `total = 61` (expected `25`), then stopped the session.

![Claude driving the debugger, step by step](images/combo-session.png)

It built this trace for the input `{5, 3, 0, 4, 2, 0, 6}`:

| round | points | combo (after) | total | should be |
|---|---|---|---|---|
| 0 | 5 | 1 | 5 | 5 |
| 1 | 3 | 2 | 11 | 11 |
| 2 | 0 | **2 ← stays!** | 11 | 11 |
| 3 | 4 | 3 | 23 | **15** (4×1) |
| 4 | 2 | 4 | 31 | **19** |
| 5 | 0 | **4 ← stays!** | 31 | 19 |
| 6 | 6 | 5 | **61** | **25** |

![The runtime trace Claude built while stepping](images/combo-trace.png)

**The diagnosis** — it caught the combo multiplier *failing to reset* on a zero round and proposed the fix:

> A **zero round doesn't reset the combo**. `if (points > 0)` and `else if (points < 0)` both miss `points == 0`, so a zero round falls through and the streak carries across it — inflating the score to 61 instead of 25. Reset the combo on a non-positive round.

![The bug and the proposed fix](images/combo-fix.png)

Nothing in the source *reads* as wrong — the flaw only surfaces when you watch `combo` stay at 2 across the zero at round 2. That's the difference between reading code and debugging it.

---

## Cornering a deadlock: LockJam

The case `vs_break_all` exists for: a **hung** program. In the [`demo/LockJam`](../demo/LockJam) fixture, five threads run against three accounts and three of them deadlock in a ring, so the process hangs forever. A deadlocked thread never *hits* a breakpoint — there's nothing to stop on, and stepping/continuing is useless. A *fresh* `claude` session was pointed at the running fixture and told only: *"the app is hung — pause it and tell me which threads are deadlocked and on what."* It drove:

- **`vs_start_debugging`** → the program runs and hangs (no break in 30 s — the deadlock signature).
- **`vs_break_all`** → pauses the hung process. The *only* way in.
- **`vs_threads`** → each stuck thread comes back with `waiting`, `waitOn: "lock"`, and a **`lockOwnerThreadId`** — parsed from the engine's `[Waiting on lock owned by Thread 0x…]` annotation. Those owner ids *are* the wait-chain.
- **`vs_evaluate('from.Id' / 'to.Id', threadId: …)`** on each stuck thread → exactly which account it holds vs. which it's blocked acquiring.

The cycle it reconstructed — straight from the tools, no source-guessing:

| Thread | holds | blocked acquiring | `lockOwnerThreadId` → |
|---|---|---|---|
| `xfer A→B` | A | B | `xfer B→C` |
| `xfer B→C` | B | C | `xfer C→A` |
| `xfer C→A` | C | A | `xfer A→B` |

A clean `A → B → C → A` ring. It also correctly **excluded** the decoys — a CPU-spinning thread (running, never waiting) and one parked on an empty semaphore (`Monitor.Wait`, but not in the cycle) — and named the fix: a consistent global lock order. The lock-ownership chain that pins the cycle comes from `vs_threads` itself; the per-thread `vs_evaluate` reads the accounts. Nothing here is inferable from the source — only from the frozen runtime state.

---

## How it reaches the model: three channels

A new IDE tool wouldn't help here. Claude Code's IDE-integration protocol (the WebSocket the CLI connects to) is **CLI-curated** — it surfaces only `getDiagnostics` (+ `executeCode`) to the model and drives the rest itself, so a 13th tool added there would never be called. Debug state therefore reaches the model through the two channels that *do*: a **hook** (push) and a **user MCP server** (pull). Driving rides the same MCP server behind a safety gate.

### 1. Push — break state at prompt time (`UserPromptSubmit` hook)

When you submit a prompt while the debugger is paused, a `UserPromptSubmit` hook (`vs-debug-context-hook.ps1`) POSTs to the bridge's `/debug-context` endpoint. The bridge reads the live break state via EnvDTE on the UI thread and hands it back; the hook injects it as `hookSpecificOutput.additionalContext`. So Claude starts the turn already knowing where you're stopped and what the values are — no tool call required. Gated on break mode: if you're not paused, nothing is injected (no noise on normal turns).

### 2. Pull — inspect on demand (the `vs-debug` MCP server)

The bridge exposes a **second MCP server** at `POST /mcp` on its localhost `HttpListener`. The CLI reaches it through a tiny stdio shim (`vs-mcp-shim.ps1`) registered in your workspace `.mcp.json` under the server name **`vs-debug`**. The shim discovers the live bridge (the most-specific workspace lockfile whose port is listening — the same hardened selection the other hooks use) and proxies newline-delimited JSON-RPC to `/mcp`. The tools themselves run **in-proc in C#** against EnvDTE; the shim is a dumb pipe.

Why a separate MCP server instead of the IDE channel? Because a user-registered `.mcp.json` server is the *open plugin door* — the CLI surfaces **all** of its tools to the model — whereas the IDE channel is a closed, curated protocol. Same `McpServer` dispatch code, a different relationship with the CLI.

### 3. Drive — control execution (same server, gated)

Execution control sits behind a panel toggle, **"Allow Claude to drive debugger"** (default **OFF**, in-memory, resets each VS session). When off, the drive tools refuse and do nothing. The hard part — a drive command is asynchronous (issue "step", then *wait for the next break*) — is handled by an await-break engine: issue the EnvDTE command with `WaitForBreakOrEnd=false` (never blocks the UI thread), subscribe to `IVsDebuggerEvents.OnModeChange`, park a `TaskCompletionSource`, and complete it on the next Break (return the fresh snapshot) or Design (program ended). A 20 s timeout reports "still running" rather than hanging.

```
                 prompt-submit          on demand                control (gated)
  Claude (CLI) ──UserPromptSubmit──▶  ──stdio JSON-RPC──▶ vs-mcp-shim.ps1
       │              hook                 (.mcp.json)            │ HTTP POST /mcp
       │                │                                        ▼
       ▼                ▼                                  IdeWebSocketServer (127.0.0.1, auth)
  /debug-context ◀──────┘                                        │
       └──────────────────────────┬─────────────────────────────┘
                                   ▼
                      DebuggerReader / DebuggerDriver  ──EnvDTE / IVsDebugger──▶  VS debugger (UI thread)
```

---

## Tool catalog

All tools live on the `vs-debug` MCP server and appear to the model as `mcp__vs-debug__*`. **Reads are ungated; drives require the "Allow Claude to drive debugger" toggle.** Most require the debugger to be paused (break mode); the snapshot otherwise reports `{"mode":"run|design|unknown"}`.

### Inspect (read-only, ungated)

| Tool | What it returns |
|---|---|
| `vs_debug_state` | Mode, stop location, call stack (innermost first), current frame's args + locals with values. |
| `vs_list_breakpoints` | All breakpoints (file, line, function, enabled, hit count, condition). Works in **any** mode. |
| `vs_get_frame_locals` | Args + locals for a call-stack `frameIndex` (walk up to callers); optional `threadId` (from `vs_threads`) reads **another thread's** frame — e.g. each thread parked in a deadlock. |
| `vs_evaluate` | Evaluate an expression in a chosen frame → `{value, type, isValid}`. |
| `vs_expand` | Drill into an object graph (`Expression.DataMembers`) to a depth → `{name,type,value,children}` tree. |
| `vs_threads` | Every thread with its call stack + location; the current thread is flagged, threads parked on a lock/wait are flagged (`waiting`/`waitOn`), and a contended-lock waiter carries `lockOwnerThreadId` (the holder — follow the chain for a deadlock cycle). |
| `vs_exception` | The exception in scope (`$exception`) at a first-chance break or in a catch — type, message, and an expanded tree incl. `InnerException` + stack. |
| `vs_list_processes` | Local processes you can attach to (id + name, optionally name-filtered), flagged if already being debugged. |

### Drive (execution & breakpoints, gated)

| Tool | Action |
|---|---|
| `vs_continue` | Resume to the next breakpoint or program end, then return the new state. |
| `vs_step_over` / `vs_step_into` / `vs_step_out` | The three step modes, each awaiting the next break. |
| `vs_run_to_line` | Run to a `file:line` (temporary breakpoint under the hood). |
| `vs_break_all` | **Pause a running/hung debuggee** (Break All) and return the new state — the way into a deadlock, which never hits a breakpoint. Needs an active session in run mode. Pair with `vs_threads` + `vs_get_frame_locals`. |
| `vs_set_breakpoint` | Set at `file:line` **or by `function` name** (break wherever a method is entered, no file:line needed), with optional `condition` and (file:line) `hitCount`/`hitCountType` (`equal`/`atLeast`/`multiple`). |
| `vs_remove_breakpoint` | Clear the breakpoint(s) at a `file:line`. |
| `vs_break_on_thrown` | Break at the **throw site** of a named managed exception (first-chance), even if it's caught — e.g. `System.NullReferenceException`. Enable/clear per type. |
| `vs_freeze_thread` | Freeze (suspend) or thaw a thread by id — isolate one thread in a race. |
| `vs_set_next_statement` | Move the execution pointer to a line without running the code in between (current method only). |
| `vs_start_debugging` / `vs_stop_debugging` | Start a session (F5, runs to the first breakpoint) / stop it (Shift+F5). |
| `vs_attach` / `vs_detach` | Attach to a **running** local process (by pid or name) — a hosted web app, service, or desktop app — then detach (it keeps running). |

### Push (no tool call)

The `UserPromptSubmit` hook injects the current break state (stop location, call stack, current-frame args/locals) into context whenever you submit a prompt while paused.

---

## Safety

- **Reads are always allowed**; **execution and breakpoint mutation are opt-in** via the panel toggle (default OFF, resets every VS session, so model-controlled execution is never silently left on). This mirrors the "Run wild (auto-accept)" toggle used for edits.
- Driving **runs your code** under model control — continue/step execute your program, and `vs_evaluate` of a method call has side effects (there is no read-only eval). That's why it's gated.
- `vs_set_next_statement` is genuinely powerful and risky (skipping initialization can corrupt state) — also behind the gate.

---

## Limitations

- **Managed (.NET) focused.** The debug reader targets the managed (CLR) debugger via EnvDTE. Native/C++ runtime inspection is not covered (C++ *build* diagnostics still flow through the Error List).
- **`vs_evaluate` has no LINQ / lambdas.** VS's expression evaluator rejects `list.Select(x => …)`. Prefer indexing, field/property access, `.Count`, `.Sum()`, `object.ReferenceEquals(a, b)`, arithmetic.
- **`vs_threads` lock ownership is best-effort (text-derived).** For a *contended lock* it surfaces the holder as `lockOwnerThreadId`, parsed from Concord's `[Waiting on lock owned by Thread 0x..]` stack annotation — follow the chain across threads for a deadlock cycle (live-verified on LockJam). But that's specific to contended monitors and depends on the engine's annotation text; other wait primitives and a frozen-state flag aren't modeled, and there's no structured ownership API (EnvDTE has none — true/all-primitive ownership would need AD7/SOS/ClrMD).
- **Async call stacks are physical-only.** Paused on an `async` continuation, the call stack is the resumed *physical* stack (`MoveNext`, async-builder internals, `ThreadPool…`), not the *logical* `InnerAsync ← ComputeAsync ← RunAsync` chain VS reconstructs in Parallel Stacks/Tasks — and a suspended async caller's hoisted locals aren't reachable by source name. Current-frame post-await locals + `vs_evaluate` work correctly (incl. with `threadId`); cross-await *caller* inspection doesn't. Future work — see ROADMAP.
- **`vs_set_next_statement` is current-method only** and moves the editor caret as a side effect (there's no direct API; it's driven through the caret + the `Debug.SetNextStatement` command).
- **Per-frame source lines are partial.** The call stack is function names; the stop file/line is the current frame only. Precise per-frame source (`IDebugStackFrame2`) is a future enhancement.
- **Output is capped, but signaled.** Large results are bounded (call stack 20 frames, locals 60, value 240 chars, threads 60, …) — but when a cap truncates, the output includes a `{"truncated": true, "note": "capped at N…"}` marker so the model knows data was cut and can narrow its query (or pass a larger `depth` to `vs_expand`). Values self-signal with a trailing `…`.
- **Data breakpoints aren't available.** "Break when *this field* changes" needs AD7/Concord and isn't exposed by EnvDTE. (Break-on-thrown — once assumed to need that lower layer — shipped via the managed `EnvDTE90.Debugger3` API; the AD7 assumption was wrong, it was just a missing cast to `Debugger3`.)
- **No native tracepoints** (log-and-continue breakpoints) yet.
- **EnvDTE is version-fragile** at the edges and throws readily during debugger transitions; every access is individually guarded, but a transient read can come back partial.

---

## Try it

Seven runnable fixtures under `demo/` exercise the feature (open the `.sln`, enable the drive toggle where noted, Launch Claude Code):

- **`CheckoutBuggy`** — an integer-division discount bug; the push hook lets Claude diagnose from the paused locals.
- **`SignalScan`** — an aliasing bug confirmable at one paused point (`vs_evaluate('object.ReferenceEquals(windows[0], windows[2])')`). No bug-revealing comments.
- **`ComboScore`** — a missing state reset that's invisible in the final state; forces stepping / a conditional breakpoint to watch `combo` across the bad iteration.
- **`NullOrigin`** — an NRE thrown deep and swallowed by a generic catch; `vs_break_on_thrown` lands you at the throw site, not the catch.
- **`WebQuote`** — an ASP.NET Core API that **stays running**, for the **attach** path: `vs_list_processes` → `vs_attach`, then `vs_break_on_thrown` and trigger `GET /quote/103` to break at the throw inside a request handler — the case F5 can't cover. Live-verified end-to-end (Claude attaches, arms, triggers the request itself, inspects, detaches).
- **`LockJam`** — five threads, three locked in a 3-node cycle (`A→B→C→A`), one merely idle, one busy: exercises the `vs_threads` wait/lock heuristic and whether Claude can isolate the cycle from the noise. Hangs on the deadlock; `vs_break_all` is the way in (a deadlocked thread never hits a breakpoint), then reads each stuck thread with `vs_get_frame_locals` + `threadId` to nail the exact cycle. The README's PASS/FAIL covers the Just-My-Code caveat that decides whether the `Monitor.Enter` flag fires.
- **`AsyncTrace`** — a three-level async pipeline whose awaits resume on the threadpool; pausing inside `InnerAsync` lands on a continuation. Confirms the current async frame's locals/`vs_evaluate` read correct post-await values, and characterizes how much of the logical async call chain the call stack surfaces.

---

## Next version

- **Test-driven debugging loop** — run the test suite; on a failing test, set a breakpoint at the fault, `vs_start_debugging` that test, and drive to the failure automatically. This composes the whole surface into an autonomous diagnose loop.
- **Native tracepoints** — log-and-continue probes Claude can sprinkle without editing the file (either VS-native if reachable, or simulated in our layer).
- **CPU / memory profiling** — `dotnet-counters` (live CPU %, GC, alloc rate), `dotnet-trace` (top hot methods), and `dotnet-gcdump` (top types by size) against the debuggee PID, surfaced as tools.
- **Per-frame source** via `IDebugStackFrame2.GetDocumentContext`.
