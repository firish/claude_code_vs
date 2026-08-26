# Roadmap

Current release ships the core protocol bridge end-to-end: native diff with Accept/Reject, single-gate via PreToolUse hook, selection context, diagnostics (C# + C++), token/cost stats panel, and one-click Launch. **1.2.0 adds live debugger integration** (push / pull / drive) — see [`docs/DEBUGGER.md`](docs/DEBUGGER.md). **1.3.0 adds attach-to-process** (debug a running web app / service / desktop app, not just F5), **break-on-thrown exceptions**, `$exception` inspection, and function breakpoints. Everything below is remaining post-1.0 work.

---

## Phase 2 - Robustness & precision

### Roslyn-precise C# diagnostic ranges

**What:** `ErrorListReader` reads from the VS Error List, which exposes only a single line/column per entry (point ranges). Roslyn has the full span - start line, start character, end line, end character - which is what LSP clients expect.

**Why it matters:** Claude uses the range to anchor its fix to a specific token. A point range works, but a precise span lets it highlight the exact bad expression rather than just the line.

**How:** MEF-import `VisualStudioWorkspace` (available in-proc) and walk `Compilation.GetDiagnostics()` per `Document`. Map `Diagnostic.Location.GetLineSpan()` -> LSP range, `DiagnosticSeverity` -> LSP severity. Use Roslyn for C#/.NET; keep the Error List path as the C++ fallback (MSVC has no Roslyn).

**Now de-risked:** the 1.9.0 `vs-semantic` work already imports `VisualStudioWorkspace` in-proc and proves the `ExcludeAssets="runtime"` binding (`CodeModel/RoslynReader.cs`). This item is now mostly "reuse that workspace handle for diagnostics + map spans."

**Caveat:** Requires a loaded project - Roslyn doesn't analyze loose files. The Error List fallback handles C++ and the open-folder case.

### Semantic-navigation follow-ups (post-1.9.0 `vs-semantic`)

**Shipped (1.9.0):** `vs_search_symbols` / `vs_find_references` / `vs_go_to_definition` / `vs_find_implementations` / `vs_call_hierarchy` / `vs_type_hierarchy` on the `vs-semantic` MCP server (`docs/SEMANTIC.md`). Remaining:

- **Transitive callees** - `vs_call_hierarchy direction:callees` is depth-1 only; reconstruct the full callee graph (cycle-guarded like callers).
- **Rename / safe refactors** - route Roslyn `Renamer`/code-fix edits through the existing diff gate, so each change is still one Accept/Reject. Crosses from read-only navigation into mutation - design the gate carefully.
- **Per-frame source for the debugger** could reuse the same workspace to map call-stack frames to precise documents.

---

### ✅ Reconnect + multi-window hardening — shipped in 1.2.0

Both failure modes below are now fixed: orphaned `openDiff` diffs are rejected and closed when the CLI disconnects (`DiffRegistry.CloseAllAsync` on `ConnectionChanged(false)`); the hooks pick the **most-specific** workspace lockfile whose port is actually **listening** (defeats parent-folder shadowing and zombie lockfiles), and lockfiles record `pidStartTime` so a recycled PID can't make a dead instance look alive. Original analysis kept for context:

**What:** The bridge picks one free port at startup and writes one lockfile. Two failure modes existed:

1. **CLI reconnect:** if the CLI disconnects (network hiccup, manual restart) and reconnects, the lockfile and server are still valid and the reconnect works - but any pending `openDiff` TCS entries are orphaned (the diff frame is still open with no live connection to answer it). The diff should be auto-rejected and closed on disconnect.

2. **Multiple VS instances:** each VS instance writes its own lockfile (different ports), so the CLI's `/ide` picker shows multiple entries. The hook's lockfile scan picks the first matching workspace, which is correct - but if two instances share the same workspace root, the scan is ambiguous.

**Fix for (1):** on `ConnectionChanged(false)`, iterate `DiffRegistry` and reject + close all open diffs, then clear the registry.

**Fix for (2):** the lockfile `workspaceFolders` path comparison in the hook already preferentially matches by cwd. Tighten it to an exact prefix match and document the multi-instance limitation clearly.

**Bonus:** stale-lockfile reap on startup is already implemented (`Lockfile.ReapStale`). The one remaining gap is if the WS server itself faults after the lockfile was written - `BridgeHost` already catches this and deletes the lockfile on an unexpected server fault. Verify this path under a forced server crash.

---

## Phase 3 - VS 2022 verification

**What:** The manifest targets `[17.14, 19.0)` (VS 2022 17.14+ through VS 2026) to satisfy Marketplace policy, but the extension has only been tested on VS 2026. VS 2022 may load it — the in-proc SDK calls (`IVsDifferenceService`, the WPF differencing factory, Roslyn workspace, `IVsRunningDocumentTable`) are not 2026-specific — but it is unverified.

**Cost:** test the Release `.vsix` on a clean VS 2022 17.14+ install and fix any API incompatibilities.

**Trigger:** do this when GitHub issue demand or download numbers justify it.

---

## Phase 4 - Embedded chat (partially shipped 1.13.0)

**What:** hosting the `claude` terminal/chat inside a VS tool window (Phase 3b in the original build plan). This would let the extension own stdin, enabling "Reject -> type reason inline" without a separate dialog, and eliminating the external console window.

**Eliminating the external console window - ✅ SHIPPED 1.13.0**, via a different route than originally planned: not a hand-rolled ConPTY + custom control (the path `dliedke/ClaudeCodeExtension` burned ~150 commits on for paste/focus/encoding/resize), but VS 2026's own native terminal engine (`Microsoft.VisualStudio.Terminal.ITerminalService`, the thing behind `View > Terminal`) - undocumented, no NuGet package, reached the same way this codebase reaches VS's internal TestWindow engine (reflection-load from the install dir, see `Terminal/VsTerminalLauncher.cs`). `claude` now runs in a docked Terminal tab instead of a separate `cmd.exe` window, with the external console kept only as a fallback if the reflection call ever fails.

**Still not done: owning stdin for "Reject -> type reason inline."** `CreateTerminalWindowAsync` hands back a `Guid`, not an `IPty`/stream handle - the extension doesn't get programmatic read/write access to the terminal's input or output this way. That would need a different call (`ITerminalService.CreatePtyAsync` / `CreateTerminalRendererAsync`, seen but not used in the reflection dump) and is a separate, larger piece of work if ever pursued.

---

## Debugger — next steps

The 1.2.0 debugger surface (see [`docs/DEBUGGER.md`](docs/DEBUGGER.md)) has clear follow-ups, roughly in priority order:

- **Data breakpoints** ("break when *this field* changes — who mutated it?") and **structured lock/wait-chain ownership** ("thread 5 is blocked on the monitor held by thread 9"). Neither is in EnvDTE at any interface version — they need AD7/Concord or out-of-proc tooling (SOS `!syncblk` via `dotnet-dump`, or ClrMD), which pairs naturally with the 1.3.0 attach path. *(Break-on-thrown — long assumed to need this lower layer — actually shipped in 1.3.0 via the managed `EnvDTE90.Debugger3.ExceptionGroups` API; the earlier "needs `IDebugEngine2.SetException`" assumption was wrong, it was just a missing cast to `Debugger3`.)*
- **Async-aware call stack.** On an `async` continuation, EnvDTE returns only the *physical* resumed stack (`InnerAsync.MoveNext → ThreadPool…`); the *logical* chain (`InnerAsync ← ComputeAsync ← RunAsync ← Main`) that VS's Parallel Stacks / Tasks windows reconstruct isn't exposed, and a *suspended* async frame's hoisted locals aren't navigable by source name. Live-characterized on `demo/AsyncTrace` (1.3.0): current-frame post-await locals + `vs_evaluate` + per-thread targeting all work, but cross-await *caller* inspection doesn't. Would need the VS Tasks/Async (Concord) debugger APIs or ClrMD/SOS — same bucket as lock-ownership above; the logical async-stack reader is the high-value piece.
- **Test-driven debugging loop — ✅ SHIPPED 1.10.0** ([`docs/TESTING.md`](docs/TESTING.md)). Discover/run/re-run-failed/debug tests through VS's Test Explorer engine, plus the flaky-hunter (`vs_hunt_flaky`) and **catch-red-handed** (`vs_catch_flaky` — loop a test under the debugger until the failing iteration halts at the throw). Remaining test follow-ups: **run tests affected by a change** (`Scope.ForFile`/`ForSymbol` + the `vs-semantic` call-graph — "run the tests that touch the code I edited"); **profiling** (wire a Diagnostics-Hub `ProfilerToolId` so `profile:true` runs under the profiler); an **`IOperationState` engine-idle wait** between hunt runs so `measureRate` never under-samples on cancellation churn.
- **Native tracepoints.** Log-and-continue probes Claude can place without editing the file. EnvDTE doesn't expose the "when hit: log + continue" action, so this is either VS-native (if reachable) or simulated in our layer (breakpoint → capture expressions on hit → auto-continue).
- **CPU / memory profiling.** Not the debugger subsystem — shell out to the .NET diagnostics CLIs against the debuggee PID: `dotnet-counters` (live CPU %, GC, alloc rate), `dotnet-trace` (top hot methods), `dotnet-gcdump` (top types by size). Surface parsed top-N results as tools.
- **Per-frame source.** Precise file/line per call-stack frame via `IDebugStackFrame2.GetDocumentContext` (today the call stack is function names + the current-stop line only).

---

## Build integration (`vs_build` + `vs_read_output`) — next steps

Shipped: the IDE's own build driven asynchronously with structured Error List diagnostics, and Output-window pane reads (`docs/BUILD.md`). Remaining, in priority order:

- **Error codes as a first-class field.** `IVsTaskItem` exposes the message text but not the `CS0103` / `MSB4018` code. The code lives in the Error List's **Code** column, which means the `Microsoft.VisualStudio.Shell.TableManager` surface (`IErrorList` → `ITableEntriesSnapshot` → `StandardTableKeyNames.ErrorCode`) rather than the task-list interface we read today. Worth it: a code is what turns an error into a lookup.
- **Build rows vs IntelliSense rows.** The same TableManager work gets `StandardTableKeyNames.ErrorSource` (`BuildTool` vs `IntelliSense`), which would let `vs_build` report *only what this build produced* and drop the `diagnosticsNote` caveat. These two are one piece of work; do them together.
- **Per-session CLI permission mode.** `BridgeStatus` holds ONE bridge-wide mode, so two owned sessions (a second terminal, a test client) fight over it: a session in auto mode locks the panel's run-wild checkbox for a session that is in manual. 1.20.0 fixed the staleness (every prompt and turn end re-samples) but not the sharing. Key it by the hook payload's session id and show the mode of the session the panel is tracking.
- **`since` bookmark on `vs_read_output`.** Return an opaque line cursor and accept it back, so polling a running app's Debug pane returns only what is new instead of re-sending the tail. The reader is already line-indexed, so this is small - the design question is whether the cursor should survive a pane clear (it should: fall back to the top, the way the build-log delta already does).
- **Run tests affected by a change** (also listed under the debugger follow-ups): `vs_build` establishes what compiled, the `vs-semantic` call graph establishes what covers it, `Scope.ForSymbol` runs it. All three pieces now exist.
- **Auto-scope to the edited project.** `vs_build` already resolves a project from any file path inside it; the model has to choose to pass one. A cheap heuristic (build the project owning the most recently edited file, fall back to the solution) would make the common case one obvious call.
- **C++ verification.** `ErrorListReader` already serves MSVC diagnostics and `demo/CheckoutDemoCpp` exists, but the build path has not been exercised against a `.vcxproj` end to end.

## Attachments (1.12.0) — next steps

Shipped: the panel attach tray (paste/drop → stage → `at_mentioned` chip in the CLI composer, token estimates, uniform any-format staging). Remaining ideas, roughly in priority order:

- **`vs_capture_window` / `vs_capture_screen` — ✅ SHIPPED 1.14.0** ([`docs/VISION.md`](docs/VISION.md)): debuggee window, VS window, window-by-title (the browser case), and full screen, ALL behind the "Allow screen capture" panel toggle, every capture staged as a visible tray chip + feed line, path returned (never MCP image blocks). Remaining capture follow-ups: **before/after capture pairs** (capture → act → capture, both paths returned) and a **region-crop parameter** for dense screens.
- **`UserPromptSubmit` auto-inject tier** — deferred by design: the chip flow is confirmed to deliver pixels, and with no ack on `at_mentioned` a second automatic channel risks double-delivery. Revisit only if real-world mid-turn drops (the click-to-re-mention path) turn out to be common.
- **Per-tool / per-subagent token breakdown in the panel.** Two halves with different truth levels: a per-SUBAGENT rollup is **exact** (sidechain messages in the transcript carry real per-request `usage` records — sum them per agent; `UsageTracker` already parses the file every turn), while per-TOOL attribution is an **estimate** (no exact per-call numbers exist anywhere, even in the CLI — group `tool_result` sizes by tool name at ~4 chars/token into a "top context consumers" line, e.g. `Read ≈40k · vs_debug_state ≈12k`). Uniquely ours: shows what the vs-debug/vs-semantic reads cost a session, pairing with the panel's inspected/driven counters. Don't duplicate what the CLI has natively (`/cost` totals, `/context` composition, subagent completion summaries). ~a day; keep the estimate labeling discipline.
- **Session-wide image/text token split in the stats card** — the API reports only aggregate `input_tokens`, so this would be estimate-on-top-of-exact (walk the transcript's image blocks, apply (w×h)/750). Deferred unless demand shows up; the per-attachment estimates cover the actionable part.

## Localization (1.16.0) — next steps

Shipped: the resx infrastructure + Simplified Chinese for the whole visible UI, following VS's display language (issue #20). Remaining, in priority order:

- **VSCT menu localization** — the two menu entries (Tools > Launch Claude Code, View > Other Windows > Claude Code) stay English: VSCT strings localize via per-language .vsct compilation or satellite UI DLLs, a disproportionate MSBuild dance for two lines. Do it if a native speaker reports it as jarring.
- **More languages on demand** — the infrastructure is one resx per language (`Strings.<culture>.resx` + a csproj `EmbeddedResource` + the satellite-bundle target picks it up generalized). Each language is a standing translation commitment (Claude maintains zh-Hans as part of every release per CLAUDE.md convention #6); add ja/ko/de only on real requests.
- **Marketplace listing in Chinese** — the VS Marketplace supports per-language listings; a translated `marketplace.md` would make the extension findable for zh users. Cheap, decoupled from the code.

## Ongoing maintenance

- **Protocol smoke test on every `claude` bump.** Run `spike/`, confirm: connects, `mcp__ide__*` tools appear, `openDiff` fires on an edit, accept/reject controls the outcome, `/permission` endpoint responds. The contract is undocumented and has broken across CLI releases before. Pin the known-good `claude --version` in the repo.
- **Backfill VS 2022** (see Phase 3 above).
- **Semver:** patch releases for protocol fixes; minor for new tool implementations; major for breaking changes to the hook interface.
