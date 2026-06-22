# Claude Code for Visual Studio

> Bring [Claude Code](https://claude.com/claude-code) into **Visual Studio 2026** - a native diff window with accept/reject, automatic selection + compiler-diagnostics context, **live debugger access**, and a stats panel. The `claude` CLI does the agent work; this extension is the **IDE half** of Claude Code's integration protocol.

![Demo](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/demo.gif)

**Status:** community project, not affiliated with Anthropic. **Visual Studio 2026 only**, for now.

---

## Why

Claude Code has first-class IDE integration for VS Code and JetBrains, but not Visual Studio - see [anthropics/claude-code#15942](https://github.com/anthropics/claude-code/issues/15942). This extension implements that same IDE-integration protocol natively for VS, so the CLI drives a real Visual Studio diff window and sees your selection and build errors - instead of you copy-pasting into a terminal.

## What you get

- **Native diff with a single accept/reject gate** - Claude's edits open in Visual Studio's diff viewer, and approving *there* is the only step (no duplicate y/n prompt in the terminal).
- **Reject with feedback** - reject an edit and tell Claude what to change; it reconsiders with your note.
- **Run wild (auto-accept)** - a panel toggle to apply edits without opening the diff, for when you want to let it cook. Resets each session.
- **Diagnostics sharing** - Claude reads Visual Studio's compiler errors/warnings (C# and C++) and fixes them.
- **Live debugger** - while you're paused at a breakpoint, Claude sees your program's runtime state (call stack, variable values, threads) and, opt-in, can *drive* the debugger (continue, step, set breakpoints, start/stop a session) to corner a bug instead of guessing from source. Full reference: **[`docs/DEBUGGER.md`](docs/DEBUGGER.md)**.
- **Selection context** - Claude automatically knows the file and lines you're looking at.
- **Live panel** - a dockable *Claude Code* panel: connection status, edit decisions, and **token usage + estimated cost** (latest call vs cumulative session).

## Watch it debug

The headline of 1.2.0. Pause at a breakpoint, ask Claude what's wrong, and with driving turned on it sets breakpoints, steps through your code, and reads the runtime values to catch bugs that never show up in the output.

Here it is on a scoring function that returns the wrong total. It stopped inside the loop and stepped through the rounds, watching a counter that should reset but didn't:

![Claude paused in the Visual Studio debugger, reading live values](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-debugger.png)

It kept a running trace of what it saw, which is how it cornered the bug:

![The runtime trace Claude built while stepping](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-trace.png)

Then the fix opened in the native diff, ready to accept or reject:

![Claude's fix in the Visual Studio diff viewer](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-diff-viewer.png)

Full walkthrough, the complete tool list, and the limitations are in **[`docs/DEBUGGER.md`](docs/DEBUGGER.md)**.

## Requirements

- **Visual Studio 2026.**
- **The Claude Code CLI**, installed and authenticated - see the [Claude Code docs](https://docs.claude.com/claude-code). *This extension makes no model calls and does no agent work itself; it requires the CLI.*
- Tested against `claude` **2.1.181**.

## Install

- **Marketplace:** search *"Claude Code for Visual Studio"* in **Extensions -> Manage Extensions**, or install from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=firish.bridgev1).
- **Sideload:** download the `.vsix` from [Releases](https://github.com/firish/claude_code_vs/releases) and double-click it.

## Quickstart

1. Open your project or solution in Visual Studio 2026.
2. Open the **Claude Code** panel (**View -> Other Windows -> Claude Code**) and click **Launch Claude Code** (also available on the **Tools** menu).
3. A terminal opens running `claude`, already connected to the IDE (no `/ide` needed). The panel pill turns green - **Connected**.
4. Ask Claude to make a change. Its edit opens as a **diff** - click **Accept**, **Reject**, or **Reject with feedback…**.

> Diagnostics need a **loaded project** (not a loose file in Open-Folder mode) for the compiler to analyze it.

> To let Claude debug: set a breakpoint, tick **Allow Claude to drive debugger** in the panel, start debugging, then ask it to investigate. Reading runtime state works without the toggle; the toggle only gates *driving* execution. See [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

## How it works

This is a **protocol bridge**, not a re-implementation of the agent. On launch the extension:

1. Starts a **localhost WebSocket server** and writes a lockfile at `~/.claude/ide/<port>.lock`.
2. Launches `claude` with `ENABLE_IDE_INTEGRATION` and the bridge port, so it auto-connects and speaks MCP / JSON-RPC over the socket.
3. Implements the IDE tools the CLI drives - `openDiff`, `openFile`, `getDiagnostics`, selection updates, and diff-tab lifecycle.

To make the **VS diff the single approval gate**, the extension installs a small **PreToolUse hook** into your workspace's `.claude/settings.json` that routes proposed edits through the diff. The CLI does all agent work; the extension never makes model calls.

For **debugger access**, it adds a `UserPromptSubmit` hook (injects live break state when you're paused) and a second MCP server, `vs-debug`, reached through a tiny stdio shim auto-registered in your workspace `.mcp.json`. Reading runtime state is always allowed; *driving* execution is opt-in behind a panel toggle. Details: **[`docs/DEBUGGER.md`](docs/DEBUGGER.md)**.

## Privacy & security

- The bridge binds to **127.0.0.1 only** and validates an **auth token** (from the lockfile) on every connection. The token is never logged.
- The extension makes **no network calls and no LLM calls of its own**. All AI work is the `claude` CLI, under your own authentication.
- On **Launch**, it writes these into your workspace's `.claude/` folder and merges hook entries into `.claude/settings.json` (preserving existing content):
  - `vs-permission-hook.ps1` - routes Edit/Write/MultiEdit edits through the VS diff.
  - `vs-usage-hook.ps1` - reports the transcript path so the panel can show token stats.
  - `vs-debug-context-hook.ps1` - injects live break state into your prompt while you're paused (a `UserPromptSubmit` hook).
  - `vs-mcp-shim.ps1` - a stdio bridge for the `vs-debug` MCP server, which it registers in your workspace `.mcp.json`.
- **Token cost is an estimate** (hardcoded per-tier prices), shown only when you click *Show est. cost*.

## Limitations & known issues

- **Visual Studio 2026 only** for now (a VS 2022 backfill is planned if there's demand).
- The IDE-integration protocol is **undocumented and version-fragile** - a `claude` update could change it. Known-good: 2.1.181.
- **Diagnostics need a loaded project** (the Error List / Roslyn won't analyze loose files).
- **Debugger features target managed (.NET) code.** Reading runtime state is always on; driving execution is opt-in. Native/C++ runtime inspection isn't covered.
- Token stats refresh **on edits** (the reliable hook trigger), so a chat-only turn may not update them immediately.
- Cost figures are **estimates**, not billing.

## Troubleshooting

- **Panel says "Waiting for CLI":** click **Launch Claude Code**, or run `/ide` in a `claude` terminal and pick *Visual Studio*.
- **New files land in the wrong folder:** launch from the extension (it pins the working directory to your workspace), or run `claude` from inside the repo.
- **getDiagnostics returns nothing:** open the code as a **project** and confirm the error appears in the **Error List**.
- **Filing a bug:** include the **Output -> Claude Code** pane contents and your `claude --version`.

## Build from source

Requires Visual Studio 2026 with the Visual Studio extension development workload.

```powershell
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Rebuild /p:Configuration=Release
```

Press F5 (or launch `devenv /rootsuffix Exp`) to debug in the experimental instance. Architecture and contributor guidance live in [`CLAUDE.md`](CLAUDE.md). Future work lives in [`ROADMAP.md`](ROADMAP.md).

## Contributing

Issues and PRs welcome. The protocol contract is undocumented and has regressed before - if you bump the `claude` CLI, please run the spike smoke test (`spike/`) and note the version.

## License

[MIT](LICENSE) © 2026 Rishi Gulati. Not affiliated with Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic, used here only to describe interoperability.
