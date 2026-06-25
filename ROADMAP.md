# Roadmap

Current release ships the core protocol bridge end-to-end: native diff with Accept/Reject, single-gate via PreToolUse hook, selection context, diagnostics (C# + C++), token/cost stats panel, and one-click Launch. **1.2.0 adds live debugger integration** (push / pull / drive) — see [`docs/DEBUGGER.md`](docs/DEBUGGER.md). **1.3.0 adds attach-to-process** (debug a running web app / service / desktop app, not just F5), **break-on-thrown exceptions**, `$exception` inspection, and function breakpoints. Everything below is remaining post-1.0 work.

---

## Phase 2 - Robustness & precision

### Roslyn-precise C# diagnostic ranges

**What:** `ErrorListReader` reads from the VS Error List, which exposes only a single line/column per entry (point ranges). Roslyn has the full span - start line, start character, end line, end character - which is what LSP clients expect.

**Why it matters:** Claude uses the range to anchor its fix to a specific token. A point range works, but a precise span lets it highlight the exact bad expression rather than just the line.

**How:** MEF-import `VisualStudioWorkspace` (available in-proc) and walk `Compilation.GetDiagnostics()` per `Document`. Map `Diagnostic.Location.GetLineSpan()` -> LSP range, `DiagnosticSeverity` -> LSP severity. Use Roslyn for C#/.NET; keep the Error List path as the C++ fallback (MSVC has no Roslyn).

**Caveat:** Requires a loaded project - Roslyn doesn't analyze loose files. The Error List fallback handles C++ and the open-folder case.

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

## Phase 4 - Embedded chat (defer)

**What:** hosting the `claude` terminal/chat inside a VS tool window (Phase 3b in the original build plan). This would let the extension own stdin, enabling "Reject -> type reason inline" without a separate dialog, and eliminating the external console window.

**Why it's deferred:** `dliedke/ClaudeCodeExtension` burned ~150 commits on paste/focus/encoding/resize trying this. The env-var handoff (the current model) is the clean separation - chat lives in an external console, diffs and context light up natively in VS.

---

## Debugger — next steps

The 1.2.0 debugger surface (see [`docs/DEBUGGER.md`](docs/DEBUGGER.md)) has clear follow-ups, roughly in priority order:

- **Data breakpoints** ("break when *this field* changes — who mutated it?") and **structured lock/wait-chain ownership** ("thread 5 is blocked on the monitor held by thread 9"). Neither is in EnvDTE at any interface version — they need AD7/Concord or out-of-proc tooling (SOS `!syncblk` via `dotnet-dump`, or ClrMD), which pairs naturally with the 1.3.0 attach path. *(Break-on-thrown — long assumed to need this lower layer — actually shipped in 1.3.0 via the managed `EnvDTE90.Debugger3.ExceptionGroups` API; the earlier "needs `IDebugEngine2.SetException`" assumption was wrong, it was just a missing cast to `Debugger3`.)*
- **Async-aware call stack.** On an `async` continuation, EnvDTE returns only the *physical* resumed stack (`InnerAsync.MoveNext → ThreadPool…`); the *logical* chain (`InnerAsync ← ComputeAsync ← RunAsync ← Main`) that VS's Parallel Stacks / Tasks windows reconstruct isn't exposed, and a *suspended* async frame's hoisted locals aren't navigable by source name. Live-characterized on `demo/AsyncTrace` (1.3.0): current-frame post-await locals + `vs_evaluate` + per-thread targeting all work, but cross-await *caller* inspection doesn't. Would need the VS Tasks/Async (Concord) debugger APIs or ClrMD/SOS — same bucket as lock-ownership above; the logical async-stack reader is the high-value piece.
- **Test-driven debugging loop.** Run the test suite; on a failing test, set a breakpoint at the fault, `vs_start_debugging` that test, and drive to the failure — composing inspect + drive into an autonomous diagnose loop. The highest-leverage next feature.
- **Native tracepoints.** Log-and-continue probes Claude can place without editing the file. EnvDTE doesn't expose the "when hit: log + continue" action, so this is either VS-native (if reachable) or simulated in our layer (breakpoint → capture expressions on hit → auto-continue).
- **CPU / memory profiling.** Not the debugger subsystem — shell out to the .NET diagnostics CLIs against the debuggee PID: `dotnet-counters` (live CPU %, GC, alloc rate), `dotnet-trace` (top hot methods), `dotnet-gcdump` (top types by size). Surface parsed top-N results as tools.
- **Per-frame source.** Precise file/line per call-stack frame via `IDebugStackFrame2.GetDocumentContext` (today the call stack is function names + the current-stop line only).

---

## Ongoing maintenance

- **Protocol smoke test on every `claude` bump.** Run `spike/`, confirm: connects, `mcp__ide__*` tools appear, `openDiff` fires on an edit, accept/reject controls the outcome, `/permission` endpoint responds. The contract is undocumented and has broken across CLI releases before. Pin the known-good `claude --version` in the repo.
- **Backfill VS 2022** (see Phase 3 above).
- **Semver:** patch releases for protocol fixes; minor for new tool implementations; major for breaking changes to the hook interface.
