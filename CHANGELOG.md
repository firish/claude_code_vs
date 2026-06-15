# Changelog

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
