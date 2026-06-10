# Phase 1 — follow-ups (post first live smoke test)

Status: **Phase 1 works.** The VSIX loads in VS 2026, the bridge auto-starts (lockfile + localhost
WS server), the CLI connects, and the **native diff + Accept/Reject InfoBar + write-back round-trips
end-to-end** against the real CLI (verified 2026-06-09, CLI 2.1.126). Selection / diagnostics / openFile
are implemented but not yet exercised live.

Connect today (no Launch command yet): set env vars to the bridge port (from the "Claude Code" output
pane, `Bridge ready on port N`) and run the CLI **inside the target repo**:
```powershell
$env:ENABLE_IDE_INTEGRATION="true"; $env:CLAUDE_CODE_SSE_PORT="<port>"; claude
```

---

## Bugs to fix

### B1. Dual prompt (terminal + diff) — RESOLVED as a known limitation; true fix is Phase 3
- **Symptom:** when the CLI proposes an edit, the accept/reject appears in *both* the terminal and our
  diff viewer; accepting in the diff doesn't dismiss the terminal prompt.
- **Root cause (confirmed via claude-code-guide + live frames):** the IDE `openDiff` review and the
  CLI's terminal edit-permission prompt are independent layers. The official VS Code/JetBrains
  integrations avoid the double prompt only by running `claude` as a **subprocess** with
  `--permission-prompt-tool stdio` + `--output-format stream-json`, routing permission INTO the IDE.
  Those flags aren't usable for an interactively-run terminal CLI.
- **`--permission-mode acceptEdits` does NOT work** — verified live: with acceptEdits the CLI
  auto-applies the edit and **never calls `openDiff`**, so the diff (our whole value) disappears.
  openDiff only fires in review-required (default) mode, which is also what shows the terminal prompt.
  So in the interactive model, diff and terminal-prompt are inseparable. Reverted to default mode.
- **Resolution:** accept the redundant terminal prompt as a Phase-1 limitation (the diff is the real
  review). The true single-gate UX requires the **subprocess + `--permission-prompt-tool` model**,
  which belongs in **Phase 3b** (embedded chat, where we own the CLI's stdio). Tracked there.

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

## Known smaller items
- Diagnostics currently come from the **Error List** (unified C#/C++). Roslyn-precise C# ranges are a
  later enhancement.
- `getDiagnostics`/`openFile` line bases assumed 0-based — verify against the CLI during T2.
