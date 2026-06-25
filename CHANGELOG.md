# Changelog

## 1.4.0 - 2026-06-25

Deadlock-triage follow-ups to the 1.3.0 debugger surface — all pure EnvDTE, no AD7.

### Features

- **`vs_break_all`** — pause a **running or hung** debuggee (Break All / Ctrl+Alt+Break) and return the new state. The way into a deadlock, which never *hits* a breakpoint so there's nothing to stop on. A gated drive tool; rides the same await-break engine (`Debugger.Break(false)` → `OnModeChange`) as continue/step.
- **Per-thread inspection** — `vs_get_frame_locals`, `vs_evaluate`, and `vs_expand` all take an optional `threadId` (from `vs_threads`): they switch `Debugger.CurrentThread` to that thread, read/evaluate, and restore — so you can read a *non-current* thread's args/locals or drill `from.Id` on each thread in a deadlock, without it being the stopped thread. Reads stay ungated.
- **Lock-chain ownership in `vs_threads`** — a thread blocked on a *contended* lock now carries `lockOwnerThreadId` (the holder), parsed from Concord's `[Waiting on lock owned by Thread 0x..]` stack annotation and converted to decimal so it cross-references another thread's `id`. Follow the chain across threads → the deadlock cycle, straight from the flags.

### Notes

- **23** `vs-debug` tools total (8 read, ungated + 15 drive, gated). Tested against `claude` 2.1.191.
- New fixtures: **`demo/LockJam`** (five threads, a 3-node deadlock cycle buried in noise — a busy thread and an idle semaphore-waiter as negative controls) and **`demo/AsyncTrace`** (cross-await inspection: locals/`vs_evaluate` on an async continuation, and characterizing how much of the logical async call stack surfaces).
- **Live-verified on LockJam (Windows VS 2026):** `vs_break_all` paused the hang, `lockOwnerThreadId` formed the cycle, and per-thread `vs_evaluate('from.Id', threadId:…)` read each account — a fully tool-grounded deadlock diagnosis. Finding: a contended lock does **not** surface a `Monitor.Enter` frame (Just-My-Code or not) — Concord replaces it with the `[Waiting on lock owned by Thread]` annotation, which the heuristic now matches.

## 1.3.0 - 2026-06-24

Headline: **debug real, running apps.** Attach to a live process — a hosted web app, a service, an already-running desktop app — instead of only F5-launching a startup project, and break at the *origin* of an exception instead of where a generic catch swallows it. Builds on the 1.2.0 debugger surface; full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **Attach to a running process** — `vs_attach` (by pid or name) + `vs_list_processes` (name-filtered) + `vs_detach`. Debug a hosted ASP.NET app (Kestrel / IIS `w3wp`), a Windows service, or an already-running desktop app — the real-app case F5 can't cover. Plain `Process.Attach()` selects the managed engine.
- **Break-on-thrown (first-chance exceptions)** — `vs_break_on_thrown` stops at the **throw site** of a named managed exception (e.g. `System.NullReferenceException`), even when a generic `catch` swallows it, so you see where it originates. Implemented via the managed `EnvDTE90.Debugger3.ExceptionGroups` API (not the low-level AD7 path).
- **Inspect `$exception`** — `vs_exception` returns the in-scope exception's type, message, and an expanded tree (incl. `InnerException` + stack) at a first-chance break or inside a catch block.
- **Function breakpoints** — `vs_set_breakpoint` now accepts a `function` name (e.g. `Namespace.Type.Method`) as an alternative to file:line — break wherever a method is entered, no source location needed. Conditions are supported.
- **Multi-process session shape** — `vs_debug_state` now reports `debuggedProcesses` (what you're attached to), surfaced in run mode too.
- **Concurrency triage** — `vs_threads` flags threads parked on a lock/wait (`waiting` / `waitOn`) to point at deadlock/contention suspects.

### Notes

- New fixture `demo/WebQuote` (ASP.NET Core) exercises attach + break-on-thrown end-to-end — verified live: the model attaches, arms break-on-thrown, triggers the request itself, lands at the throw site, inspects, and detaches.
- **22** `vs-debug` tools total (8 read, ungated + 14 drive, gated). Reading runtime state stays ungated; attach/detach, break-on-thrown, and execution control are gated behind the "Allow Claude to drive debugger" toggle.
- Tested against `claude` 2.1.186.

---

## 1.2.0 - 2026-06-17

Headline: **live debugger integration** — Claude can now see your program's runtime state, and (opt-in) drive the debugger to corner a bug instead of guessing from source. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **Debugger awareness (push)** - when you submit a prompt while paused at a breakpoint, a `UserPromptSubmit` hook injects the live break state (stop location, call stack, current-frame arguments and locals with values) into Claude's context. No tool call needed; gated on break mode so normal turns stay quiet.
- **Runtime inspection (pull)** - a second MCP server, `vs-debug`, exposes on-demand read tools the model can call mid-turn: `vs_debug_state`, `vs_evaluate`, `vs_expand` (drill into an object graph), `vs_get_frame_locals`, `vs_list_breakpoints`, `vs_threads`. Reached via a tiny stdio shim auto-registered in the workspace `.mcp.json`; the tool logic runs in-proc against EnvDTE.
- **Drive the debugger (opt-in)** - behind a new **"Allow Claude to drive debugger"** panel toggle (default off, resets each session): `vs_continue`, `vs_step_over`/`into`/`out`, `vs_run_to_line`, `vs_set_breakpoint` (with condition + hit count), `vs_remove_breakpoint`, `vs_freeze_thread`, `vs_set_next_statement`, and `vs_start_debugging`/`vs_stop_debugging`. An await-break engine (`IVsDebuggerEvents.OnModeChange` + a parked completion) returns the new state after each step without blocking the UI thread.
- **Truncation signaling** - capped results (call stack, locals, threads, expanded members) now carry a `{truncated: true, …}` marker so Claude knows data was cut and can narrow its query, instead of silently seeing a partial picture.
- **Panel debugger stat** - the dockable panel's stats card now shows session attribution: *N inspected · M driven*.
- **Multi-instance + reconnect hardening** - hooks pick the most-specific workspace lockfile whose port is actually listening (defeats parent-folder shadowing and zombie lockfiles); lockfiles record `pidStartTime` so a recycled PID can't make a dead instance look alive; orphaned diffs are rejected and closed when the CLI disconnects.

### Known limitations

- Managed (.NET) debugging only; native/C++ runtime inspection is out of scope.
- `vs_evaluate` can't evaluate LINQ/lambda expressions (VS evaluator limitation).
- `vs_threads` gives per-thread stacks but not lock/wait-chain ownership.
- Break-on-thrown exceptions and native tracepoints are not yet implemented (planned - see `ROADMAP.md`).
- Tested against `claude` 2.1.181.

---

## 1.0.1 - 2026-06-15

### Fixes

- Fixed README demo GIF path to absolute URL so it renders on the VS Marketplace listing.
- Added VS Marketplace and GitHub Releases links to README.
- Added VSIX icon and preview image.
- Corrected manifest publisher display name and version range floor for Marketplace compliance.

---

## 1.0.0 - 2026-06-15

Initial release. Implements the full Claude Code IDE-integration protocol for Visual Studio 2026.

### Features

- **Native diff with single-gate accept/reject** - Claude's proposed edits open in Visual Studio's diff viewer (not a terminal y/n). A PreToolUse hook intercepts every Edit/Write/MultiEdit and routes it through the diff; the CLI writes the file only after you accept. No duplicate terminal prompt.
- **Reject with feedback** - an InfoBar action in the diff that prompts for a reason; the reason is returned to the CLI as `permissionDecisionReason` so Claude reconsiders.
- **Run wild (auto-accept)** - a panel checkbox that allows edits without opening the diff, for unattended sessions. Resets each VS session.
- **Diagnostics sharing** - `getDiagnostics` reads the VS Error List (Roslyn for C#, MSVC toolchain for C++) and returns LSP-shaped diagnostics so Claude can see and fix your build errors.
- **Selection context** - a `selection_changed` notification (150 ms debounce) keeps Claude aware of the active file and highlighted lines in real time.
- **One-click Launch** - *Tools -> Launch Claude Code* opens a terminal pre-wired with `ENABLE_IDE_INTEGRATION` and the bridge port, working directory set to the solution root. Auto-installs the permission and usage hooks on first launch.
- **Dockable Claude Code panel** - connection status pill, accept/reject edit counts, token usage (input / output / cached) and estimated cost (latest call + cumulative session), pending-diff strip, and a curated activity feed. VS-theme-aware (dark/light).
- **Full 12-tool parity** - all tools advertised in `tools/list`: `openFile`, `openDiff`, `getCurrentSelection`, `getLatestSelection`, `getDiagnostics`, `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty`, `saveDocument`, `close_tab`, `closeAllDiffTabs`. `executeCode` returns an honest MCP error (no VS equivalent).
- **RDT-aware write-back** - accepting an edit updates an open editor buffer in place (no reload prompt) via `IVsRunningDocumentTable`.
- **Lockfile lifecycle** - stale (dead-PID) lockfiles are reaped on startup; the lockfile is deleted on clean shutdown and on an unexpected server fault.
- **WorkspaceWatcher** - keeps the lockfile's `workspaceFolders` in sync as solutions open/close so `/ide` always matches the current working directory.

### Known limitations

- Visual Studio 2026 only (VS 2022 backfill planned - see `ROADMAP.md`).
- Diagnostic ranges are point ranges (Error List only exposes line/column); Roslyn-precise spans are a future enhancement.
- The IDE-integration protocol is undocumented and version-fragile. Tested against `claude` 2.1.173.
- Token stats refresh on edits (the reliable hook trigger); a chat-only turn may not update them immediately.
- Cost figures are estimates (hardcoded per-tier list prices), not billing.
