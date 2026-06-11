# ClaudeCodeVS — status & follow-ups

Status (2026-06-10, CLI 2.1.172): **Phase 1 + the valuable parts of Phase 2 are done and proven.**
Working end-to-end against the real CLI in VS 2026:
- Native **diff + Accept/Reject + write-back** (RDT-aware: updates an open editor buffer in place, no
  reload prompt).
- **Diagnostics** — the #15942 loop verified: Claude detects a real CS0029, opens a diff to fix it,
  accept, re-queries clean. (Error List backend; needs a *loaded project*, not a loose file.)
- **Selection context** via the `selection_changed` push; **one-click Launch Claude Code** command;
  `close_tab`/`closeAllDiffTabs` real; lockfile lifecycle + reap + delete-if-server-faults.

**Ceiling reached:** the CLI exposes only `getDiagnostics` + `executeCode` to the model and drives
diff/openFile/selection internally; the awareness tools are dormant by design (see the IMPORTANT
section below). So further agent capability isn't available — remaining work is **UX (Phase 3)** and
robustness, not new tools.

Phase 2 deferred/optional: Roslyn-precise diagnostic spans, C++ diagnostics test, reconnect/multi-
window hardening, theme-aware diff chrome.

**Phase 3a panel — themed + stats (done).** The dockable panel is now VS-theme-aware (dark/light via
`VsBrushes`), with a status pill, toolbar (Launch / run-wild / Clear / Output), a stats card, a
pending-diff strip, and a curated activity feed (raw frames stay in the Output pane). Stats: edit
decisions (accepted/rejected) and **token usage + estimated cost**, shown as **Latest** (most recent
API call) and **Session** (cumulative). Cost sits behind a toggle and is labelled an estimate.
- **Usage source:** the IDE protocol carries no token data, so we parse the CLI **transcript JSONL**
  (`UsageTracker`). The transcript path arrives via hooks. The **Stop hook installs but doesn't fire
  reliably**, so usage is refreshed by piggybacking the **transcript_path on the /permission hook**
  (fires on every edit) — the reliable trigger. Stop hook kept as a backup.
- **Token display nuance:** "in" = `input_tokens + cache_creation` (input_tokens alone is ~1 under
  prompt caching and reads as a bug); "cached" = `cache_read`. Cost prices are hardcoded per tier
  (Opus/Sonnet/Haiku, ~Jan 2026) — an estimate, not a bill.
- **Panel gotcha:** subscribe on `Loaded` / unsubscribe on `Unloaded` (VS fires Unloaded on hide/dock,
  so ctor-only subscription freezes the panel). Themed dialogs need explicit brushing — `DialogWindow`
  themes the chrome but not hand-built content. CheckBox/label text needs an explicit themed Foreground.

---

## Phase 1 history (kept for reference)

The original Phase-1 smoke test (2026-06-09, CLI 2.1.126) proved the diff round-trip end-to-end.

Connect today (no Launch command yet): set env vars to the bridge port (from the "Claude Code" output
pane, `Bridge ready on port N`) and run the CLI **inside the target repo**:
```powershell
$env:ENABLE_IDE_INTEGRATION="true"; $env:CLAUDE_CODE_SSE_PORT="<port>"; claude
```

---

## Bugs to fix

### B1. Dual prompt — SOLVED via a PreToolUse hook (no subprocess/chat needed)
- **What didn't work:** `--permission-prompt-tool` is interactive-mode-only-NO (headless only, issue
  #1429); it can't target our IDE WS tools. `--permission-mode acceptEdits` AND any pre-approval (incl.
  a hook returning allow) suppress `openDiff` entirely. So "keep openDiff + drop terminal prompt" is
  impossible.
- **What works (shipped, verified CLI 2.1.173):** a **PreToolUse hook** on Edit/Write/MultiEdit becomes
  the single gate. The hook reconstructs the proposed file from `tool_input`, POSTs `{filePath,
  newContents}` to the bridge's auth-gated **`POST /permission`** endpoint, which shows our native diff
  (review-only, `writeBack:false`) and returns allow/deny from Accept/Reject. The hook emits that as the
  PreToolUse `permissionDecision`. Result: **no terminal prompt, our diff is the only gate**; on allow
  the CLI writes the file itself. Fail-open on any error (never blocks the CLI). Hook `timeout` set to
  24h so the diff can wait for an unattended user; the model is idle (no API timeout) while waiting.
- **Status: DONE + productionized.** The Launch command auto-installs the hook (embedded script +
  merges a PreToolUse entry into the workspace `.claude/settings.json`, idempotent), 24h hook timeout.
  Plus: **reject-with-reason** (a "Reject with feedback" diff action whose text becomes the hook's
  `permissionDecisionReason`, so Claude reconsiders) and **run-wild** (a panel "Auto-accept" checkbox
  that makes `/permission` allow without a diff; resets each VS session). After an allow, the open doc
  auto-reloads (if clean) so the editor refreshes immediately.
- **UTF-8 gotcha (cost several rounds):** the hook path has FOUR encoding points and all must be UTF-8,
  or non-ASCII content (em-dashes, smart quotes) silently breaks: (1) the hook's POST body bytes,
  (2) the bridge's body read, (3) the hook's `Get-Content` of the file (PS 5.1 defaults to ANSI),
  (4) the hook's STDIN read of the payload (default console input encoding). #4 garbles `old_string`,
  so reconstruction matches nothing and the diff shows 0 changes even though Claude writes correctly.

### B2. New files land in the CLI's cwd, not the VS workspace
- **Symptom:** asked to create a file, the CLI wrote it to `C:\Users\rgulati\source\repos` instead of
  the open repo.
- **Cause:** the CLI resolves file paths relative to **its own working directory**. We connected via
  env var from a terminal whose cwd was elsewhere. Not an extension bug — but bad UX.
- **Action:** the **Launch command (T1)** must start the CLI with `WorkingDirectory` = the VS workspace
  root. Until then, document: run `claude` inside the repo you want to edit.

---

## Tasks

### T1. "Launch Claude Code" command  (also fixes B2 + the /ide cwd-matching pain)
- VS menu command (Tools / or a toolbar button). On invoke: open a terminal with
  `ENABLE_IDE_INTEGRATION=true`, `CLAUDE_CODE_SSE_PORT=<our port>`, and **WorkingDirectory = workspace
  root**, running `claude`. This makes connection one click (no `/ide`, no port copy, correct cwd).
- Needs a `.vsct` (command table) + command handler. This is the one bit of Phase 1 still deferred.

### T2. Verify selection + diagnostics live
- `getCurrentSelection` / `selection_changed`: select code, ask "what did I select?".
- `getDiagnostics`: introduce a compile error, ask "what errors?". (Error List backend; C# + C++.)

---

## Suggestion 3 — persistent dockable panel (ANSWER: yes, Phase 3, split in two)

The transient comparison window "pops up and closes" per edit. A Claude Code-style **dockable tool
window** is the right long-term direction and is exactly what feature request #15942 asks for. It is
**Phase 3** and sequenced last on purpose — hosting an interactive terminal/chat inside a VS tool
window is the highest-risk piece (the existing terminal-based extension spent ~150 commits on
paste/focus/encoding/resize). Split it:

- **3a (moderate effort, high value):** a dockable tool window that hosts the **diff + Accept/Reject +
  status/log rendered inside it** — using `IWpfDifferenceViewerFactoryService` over an
  `IDifferenceBuffer` (build-plan §5) instead of the transient `IVsDifferenceService` window. The diff
  stays open and docked; chat still lives in a terminal.
- **3b (risky, defer):** embed the `claude` terminal/chat itself in the tool window.

Recommendation: do 3a after Phase 2 robustness; attempt 3b only if adoption justifies it.

**Reject-with-reason (requires 3b).** The diff InfoBar is Accept/Reject only, because the `openDiff`
result can carry only `DIFF_ACCEPTED`/`DIFF_REJECTED` — a *reason* is a chat message and chat input
goes through the CLI's stdin (the terminal), which we don't own in the terminal model. There is no
"send user message" in the IDE protocol (our notifications are one-way context). Once 3b hosts chat
I/O we own stdin, so a "Reject → type why" button can inject the message. Until then: reject in the
diff, then type the reason in the terminal when Claude asks.

---

## Dev-loop gotchas learned (so the build/deploy doesn't fight us)
- **Close every experimental instance before deploying.** A running Exp instance blocks
  `EnableExtension` (`VSSDK1031`) and locks the deployed DLLs → broken/partial deploy → load failure.
- **Reload the solution in the IDE after files are added on disk.** The legacy csproj uses `**/*.cs`
  globbing, which the IDE evaluates at load time only — new files (e.g. `WorkspaceWatcher.cs`) won't
  compile in the IDE until a reload (command-line builds pick them up fine). Consider switching to
  explicit `<Compile Include>` items if this keeps biting.
- **Don't run command-line builds of the VSIX while iterating in the IDE** — they desync VS's
  up-to-date check so F5 skips build+deploy. Pick one path (IDE F5 *or* CLI) per session.
- The bridge **picks a new free port each launch**; the env-var connect value changes accordingly.

## IMPORTANT: which IDE tools the CLI actually uses (verified 2026-06-10, CLI 2.1.172)

Live frame logs show the CLI only ever *calls* a SUBSET of the advertised tools. Its IDE awareness is:
- **`selection_changed`** (notification we push) → the active file + selection. This is how Claude
  knows the "active/selected file" — NOT via getCurrentSelection.
- **`getDiagnostics`** → errors/warnings (called constantly).
- **`openDiff` / `openFile` / `close_tab` / `closeAllDiffTabs`** → the diff & navigation flow.

Tools the current CLI NEVER calls: `getOpenEditors`, `checkDocumentDirty`, `saveDocument`,
`getWorkspaceFolders`, `getCurrentSelection`, `getLatestSelection`. They're implemented and correct
(full-parity), but **dormant**. Keep them (a future CLI may use them, and the official VS Code
extension advertises all 12 too), but don't expect "what files are open?" / "is this unsaved?" to work
via Claude.

**CONFIRMED mechanism (CLI 2.1.172, via Claude's own tool introspection):** we advertise all 12 in
tools/list (visible in the handshake log), but the CLI only exposes a fixed subset to the *model*. The
model-callable IDE tools are exactly **`getDiagnostics` + `executeCode`**. `openDiff` / `openFile` /
`close_tab` / `closeAllDiffTabs` / `selection_changed` are driven by the CLI internally (not model
choices). So advertising more tools or improving descriptions cannot make the model use them — the CLI
controls the model's tool surface, not us. This caps the agent-capability ceiling of Approach C at:
**diff flow + selection context + diagnostics** — all of which now work.

**Consequence for the value prop:** the real, CLI-exercised surface is **selection context +
diagnostics + the diff flow**. That's where to invest. Diagnostics is the headline (#15942), so making
getDiagnostics return real, precise errors matters most.

### getDiagnostics needs a loaded project
A loose `.cs` in an Open-Folder isn't analyzed by Roslyn, so the Error List stays empty and
getDiagnostics returns `[]` (observed with `test2.cs`). It only populates for files in a **loaded
project**. `diag-test/` (a minimal console project with a deliberate CS0029) exists to verify this:
open it as a project in the experimental VS, confirm the Error List shows the error, then ask Claude.
If the Error List shows it but getDiagnostics still returns `[]`, that's a real bug in ErrorListReader
to chase (the IVsTaskList read). This is the key Phase-2 thing left to validate.

## Known smaller items
- Diagnostics currently come from the **Error List** (unified C#/C++). Roslyn-precise C# ranges are a
  later enhancement.
- `getDiagnostics`/`openFile` line bases assumed 0-based — verify against the CLI during T2.
