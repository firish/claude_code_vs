# Roadmap

Current release ships the core protocol bridge end-to-end: native diff with Accept/Reject, single-gate via PreToolUse hook, selection context, diagnostics (C# + C++), token/cost stats panel, and one-click Launch. Everything in this file is post-1.0 work.

---

## Phase 2 - Robustness & precision

### Roslyn-precise C# diagnostic ranges

**What:** `ErrorListReader` reads from the VS Error List, which exposes only a single line/column per entry (point ranges). Roslyn has the full span - start line, start character, end line, end character - which is what LSP clients expect.

**Why it matters:** Claude uses the range to anchor its fix to a specific token. A point range works, but a precise span lets it highlight the exact bad expression rather than just the line.

**How:** MEF-import `VisualStudioWorkspace` (available in-proc) and walk `Compilation.GetDiagnostics()` per `Document`. Map `Diagnostic.Location.GetLineSpan()` -> LSP range, `DiagnosticSeverity` -> LSP severity. Use Roslyn for C#/.NET; keep the Error List path as the C++ fallback (MSVC has no Roslyn).

**Caveat:** Requires a loaded project - Roslyn doesn't analyze loose files. The Error List fallback handles C++ and the open-folder case.

---

### Reconnect + multi-window hardening

**What:** The bridge picks one free port at startup and writes one lockfile. Two failure modes exist today:

1. **CLI reconnect:** if the CLI disconnects (network hiccup, manual restart) and reconnects, the lockfile and server are still valid and the reconnect works - but any pending `openDiff` TCS entries are orphaned (the diff frame is still open with no live connection to answer it). The diff should be auto-rejected and closed on disconnect.

2. **Multiple VS instances:** each VS instance writes its own lockfile (different ports), so the CLI's `/ide` picker shows multiple entries. The hook's lockfile scan picks the first matching workspace, which is correct - but if two instances share the same workspace root, the scan is ambiguous.

**Fix for (1):** on `ConnectionChanged(false)`, iterate `DiffRegistry` and reject + close all open diffs, then clear the registry.

**Fix for (2):** the lockfile `workspaceFolders` path comparison in the hook already preferentially matches by cwd. Tighten it to an exact prefix match and document the multi-instance limitation clearly.

**Bonus:** stale-lockfile reap on startup is already implemented (`Lockfile.ReapStale`). The one remaining gap is if the WS server itself faults after the lockfile was written - `BridgeHost` already catches this and deletes the lockfile on an unexpected server fault. Verify this path under a forced server crash.

---

## Phase 3 - VS 2022 backfill

**What:** The manifest currently pins `[18.0, 19.0)` (VS 2026 only). VS 2022 (version 17.x) has a much larger installed base.

**Cost:** widen the manifest version range to `[17.0, 19.0)` and retest the in-proc SDK calls against the VS 2022 SDK - `IVsDifferenceService`, the WPF differencing factory, Roslyn workspace, and `IVsRunningDocumentTable`. Nothing in the protocol or architecture is 2026-specific; this is a test-matrix exercise, not a fork.

**Trigger:** do this when GitHub issue demand or download numbers justify it.

---

## Phase 4 - Embedded chat (defer)

**What:** hosting the `claude` terminal/chat inside a VS tool window (Phase 3b in the original build plan). This would let the extension own stdin, enabling "Reject -> type reason inline" without a separate dialog, and eliminating the external console window.

**Why it's deferred:** `dliedke/ClaudeCodeExtension` burned ~150 commits on paste/focus/encoding/resize trying this. The env-var handoff (the current model) is the clean separation - chat lives in an external console, diffs and context light up natively in VS.

---

## Ongoing maintenance

- **Protocol smoke test on every `claude` bump.** Run `spike/`, confirm: connects, `mcp__ide__*` tools appear, `openDiff` fires on an edit, accept/reject controls the outcome, `/permission` endpoint responds. The contract is undocumented and has broken across CLI releases before. Pin the known-good `claude --version` in the repo.
- **Backfill VS 2022** (see Phase 3 above).
- **Semver:** patch releases for protocol fixes; minor for new tool implementations; major for breaking changes to the hook interface.
