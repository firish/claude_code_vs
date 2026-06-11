# Single-gate auto-install test

This folder starts with **no** `.claude/`. When you open it as the workspace and run
**Tools → Launch Claude Code**, the extension should auto-create `.claude/settings.json` (with the
PreToolUse hook) and `.claude/vs-permission-hook.ps1`. Then ask Claude to edit this file and confirm
the single-gate diff (no terminal prompt).
