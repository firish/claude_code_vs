# CLAUDE.md

Operating context for Claude Code when working in this repo. Full rationale and roadmap live in `build-plan.md` — read it before large changes.

## What this is

A native **Visual Studio 2026 extension** that launches the real `claude` CLI and implements Claude Code's **IDE-integration protocol** (lockfile + localhost WebSocket speaking MCP/JSON-RPC 2.0). The CLI does all agent work; this extension provides the IDE half: a **native diff window with accept/reject** and **automatic selection + diagnostics context**. We do *not* reimplement the agent, and we do *not* build skills/plugins/hooks — those come from the CLI for free.

If you ever find yourself adding an LLM API call, an agent loop, or a tool the CLI already provides, stop — that's out of scope (that's the failed "Approach B"; see `build-plan.md` §1).

## Architecture (where things live)

- `src/Protocol/` — lockfile writer, WS server, MCP handshake, JSON-RPC framing.
- `src/Tools/` — one `IIdeTool` per protocol tool (openDiff, getCurrentSelection, getDiagnostics, openFile, …).
- `src/Diff/` — diff rendering + accept/reject InfoBar + write-back.
- `src/Launcher/` — spawns `claude` with the right env.
- `spike/` — Phase 0 standalone console harness (net8.0), kept for protocol regression testing.

## Tech stack & hard constraints

- **In-proc VSIX, `net48`, VSSDK + Community Toolkit.** The differencing service, Roslyn workspace, RDT, and editor adapters are in-proc services; the out-of-process `VisualStudio.Extensibility` model can't host them. Do not propose migrating the diff core to it.
- **WebSocket = `HttpListener`** bound to `127.0.0.1` only. No third-party WS/agent libraries.
- **Target VS 2026 only for now** (matches where #15942 points; halves the test matrix). Pin the manifest version range to 2026; backfill VS 2022 once Phase 1 proves out — see `build-plan.md` §8.

## Non-negotiable conventions

1. **Threading.** The WS receive loop runs off-thread. *Every* call that touches the editor, solution, diff, or any VS service must first:
   ```csharp
   await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
   ```
   This is the #1 source of bugs. Never call VS SDK APIs from the socket thread directly.
2. **Localhost + auth only.** Bind to `127.0.0.1`. Validate `x-claude-code-ide-authorization` against the lockfile token during the HTTP upgrade; reject mismatches with 401 before the socket opens. Never log the auth token.
3. **`openDiff` is deferred.** Do not reply to the `tools/call` until the user accepts/rejects. Park the response on a `TaskCompletionSource` keyed by `tab_name`; complete it from the Accept/Reject handlers. Returning early breaks the flow.
4. **Return-value wire format.** Plain strings are sent verbatim (`"DIFF_ACCEPTED"`, `"DIFF_REJECTED"`, `"FILE_SAVED"`, `"TAB_CLOSED"`); objects are JSON-wrapped; errors use the MCP `isError` flag.
5. **Lockfile lifecycle.** Write on connect, delete on shutdown, reap stale (dead-PID) lockfiles on startup. A stale lockfile with a dead socket blocks reconnection — tie lockfile lifetime to the WS connection.

## Protocol quick reference

Lockfile `~/.claude/ide/<port>.lock` (filename == port):
```json
{ "pid": 0, "workspaceFolders": ["..."], "ideName": "Visual Studio",
  "transport": "ws", "runningInWindows": true, "authToken": "<uuid>" }
```
Env before launching CLI: `CLAUDE_CODE_SSE_PORT=<port>`, `ENABLE_IDE_INTEGRATION=true`.
Full schema + all 12 tool definitions: `build-plan.md` §3–§4.

**WS handshake (verified vs CLI 2.1.169, spike):** the upgrade request carries `Sec-WebSocket-Protocol: mcp` — **echo it in the 101 response or the CLI drops the socket before `initialize`**. MCP `protocolVersion` is `2025-11-25` (echo the client's). After `initialize`+`notifications/initialized`, the CLI sends an `ide_connected` notification `{pid}` and proactively calls `closeAllDiffTabs`. Full details: `build-plan.md` §3.

## Tool status

Core 4 (the 90%): `openFile`, `openDiff`, `getCurrentSelection`, `getDiagnostics` + `selection_changed` notification.
Parity tools (later): `getLatestSelection`, `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty`, `saveDocument`, `close_tab`, `closeAllDiffTabs`, `executeCode` (→ MCP error; no VS equivalent).

- [x] Phase 0 spike (console) — protocol verified end-to-end vs CLI 2.1.169 (handshake + openDiff accept/reject round-trip). See `build-plan.md` §7.
- [ ] Phase 1 core 4 in VSIX
- [ ] Phase 2 full parity + robustness
- [ ] Phase 3 embedded chat (deferred)

## Diagnostics: two backends

- C#/.NET → Roslyn `VisualStudioWorkspace` (`Compilation.GetDiagnostics()`) for precise ranges.
- C++ → Error List (`SVsErrorList`/`ErrorListProvider`); coarser but it's the primary audience (feature request #15942), so keep it solid.
- Always return `[{uri, diagnostics: []}]` — the envelope, even when empty.

## Build / run / test

> Fill in actual commands as the project takes shape.

```bash
# Spike (Phase 0) — fastest protocol loop
dotnet run --project spike
#   then in a terminal with the env vars set:  claude   ->   /ide

# Extension
msbuild ClaudeCodeVS.sln /p:Configuration=Debug   # or: dotnet build
# F5 in VS launches the experimental instance with the VSIX loaded.
```

Protocol smoke test on every CLI bump (the contract is undocumented and has regressed before):
```bash
claude --version            # record the known-good version
# launch spike, run /ide, confirm: connects, lists mcp__ide__* tools,
# openDiff fires on an edit, accept/reject controls the outcome.
```

## Gotchas

- **`Sec-WebSocket-Protocol: mcp` must be echoed** in the WS upgrade response. Without it the CLI connects (auth OK) then silently drops before `initialize` — looks like a mysterious disconnect. Undocumented; spike-confirmed vs CLI 2.1.169.
- Contract is **undocumented and version-fragile** — pin `claude --version`, smoke-test on every bump.
- `runningInWindows: true` changes how the CLI checks PID liveness (`tasklist.exe` vs `ps`).
- `new_file_contents` in `openDiff` is in-memory → write to a temp file to feed the comparison; write to `new_file_path` only on Accept.
- Debounce `selection_changed` (~100–200ms) or you'll flood the socket.
