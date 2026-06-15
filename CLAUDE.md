# CLAUDE.md

Operating context for Claude Code when working in this repo. Future work and release plan live in `ROADMAP.md`.

## What this is

A native **Visual Studio 2026 extension** that launches the real `claude` CLI and implements Claude Code's **IDE-integration protocol** (lockfile + localhost WebSocket speaking MCP/JSON-RPC 2.0). The CLI does all agent work; this extension provides the IDE half: a **native diff window with accept/reject** and **automatic selection + diagnostics context**. We do *not* reimplement the agent, and we do *not* build skills/plugins/hooks - those come from the CLI for free.

If you ever find yourself adding an LLM API call, an agent loop, or a tool the CLI already provides, stop - that's out of scope.

## Working agreement (how we collaborate here)

- **Build in chunks, then teach.** Run free on a defined chunk of work (a phase or a well-scoped task), then - before moving on - explain everything the user needs to know/learn about what was built. The user is shipping this *and* learning the domain (VS SDK, the Claude Code IDE protocol) along the way, so keep code and decisions explainable and don't bury rationale.
- **Ask before design decisions with tradeoffs.** When a fork has real tradeoffs (not a choice with an obvious default), surface it with a recommendation and let the user decide. Decisions clearly load-bearing in the existing code don't need re-asking.
- **Ask when an instruction is unclear** rather than guessing and running.

## Architecture (where things live)

- `src/ClaudeCodeVS.Protocol/` - lockfile writer, WS server, MCP/JSON-RPC framing.
- `src/ClaudeCodeVS/Tools/` - one `IIdeTool` per protocol tool (openDiff, openFile, getCurrentSelection, getDiagnostics, …).
- `src/ClaudeCodeVS/Diff/` - diff rendering + Accept/Reject InfoBar + write-back + tab registry.
- `src/ClaudeCodeVS/Editor/` - selection service + TextViewListener MEF component + Error List reader + RDT helpers.
- `src/ClaudeCodeVS/Hooks/` - hook installer + embedded `vs-permission-hook.ps1` + `vs-usage-hook.ps1`.
- `src/ClaudeCodeVS/Ui/` - dockable panel (BridgeStatus state, ClaudeToolWindowControl WPF, ReasonDialog).
- `BridgeHost.cs` - wires everything together; owns the `/permission` handler and CLI launcher.
- `spike/` - Phase 0 standalone console harness (net8.0), kept for protocol regression testing.

## Tech stack & hard constraints

- **In-proc VSIX, `net48`, VSSDK + Community Toolkit.** The differencing service, Roslyn workspace, RDT, and editor adapters are in-proc services; the out-of-process `VisualStudio.Extensibility` model can't host them. Do not propose migrating the diff core to it.
- **WebSocket = `HttpListener`** bound to `127.0.0.1` only. No third-party WS/agent libraries.
- **Target VS 2026 only for now** (matches where #15942 points; halves the test matrix). Pin the manifest version range to `[18.0, 19.0)`; backfill VS 2022 when demand justifies it - see `ROADMAP.md`.

## Non-negotiable conventions

1. **Threading.** The WS receive loop runs off-thread. *Every* call that touches the editor, solution, diff, or any VS service must first:
   ```csharp
   await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
   ```
   This is the #1 source of bugs. Never call VS SDK APIs from the socket thread directly.
2. **Localhost + auth only.** Bind to `127.0.0.1`. Validate `x-claude-code-ide-authorization` against the lockfile token during the HTTP upgrade; reject mismatches with 401 before the socket opens. Never log the auth token.
3. **`openDiff` is deferred.** Do not reply to the `tools/call` until the user accepts/rejects. Park the response on a `TaskCompletionSource` keyed by `tab_name`; complete it from the Accept/Reject handlers. Returning early breaks the flow.
4. **Return-value wire format.** Plain strings are sent verbatim (`"DIFF_ACCEPTED"`, `"DIFF_REJECTED"`, `"FILE_SAVED"`, `"TAB_CLOSED"`); objects are JSON-wrapped; errors use the MCP `isError` flag.
5. **Lockfile lifecycle.** Write on connect, delete on shutdown, reap stale (dead-PID) lockfiles on startup. A stale lockfile with a dead socket blocks reconnection - tie lockfile lifetime to the WS connection.

## Protocol quick reference

Lockfile `~/.claude/ide/<port>.lock` (filename == port):
```json
{ "pid": 0, "workspaceFolders": ["..."], "ideName": "Visual Studio",
  "transport": "ws", "runningInWindows": true, "authToken": "<uuid>" }
```
Env before launching CLI: `CLAUDE_CODE_SSE_PORT=<port>`, `ENABLE_IDE_INTEGRATION=true`.
Full schema + all 12 tool definitions: see `src/ClaudeCodeVS/Tools/` and the Tool status section below.

**WS handshake (verified vs CLI 2.1.169, spike):** the upgrade request carries `Sec-WebSocket-Protocol: mcp` - **echo it in the 101 response or the CLI drops the socket before `initialize`**. MCP `protocolVersion` is `2025-11-25` (echo the client's). After `initialize`+`notifications/initialized`, the CLI sends an `ide_connected` notification `{pid}` and proactively calls `closeAllDiffTabs`. Implementation: `IdeWebSocketServer.cs` + `McpServer.cs`.

## Tool status

All 12 tools are implemented. The CLI exposes only `getDiagnostics` + `executeCode` to the model; `openDiff`, `openFile`, `close_tab`, `closeAllDiffTabs`, and `selection_changed` are driven by the CLI internally (not model choices). The remaining awareness tools are implemented and correct but dormant in the current CLI.

| Tool | Status |
|---|---|
| `openFile` | ✅ real |
| `openDiff` | ✅ real - deferred TCS, InfoBar, write-back |
| `getCurrentSelection` / `getLatestSelection` | ✅ real |
| `getDiagnostics` | ✅ real - Error List backend (C# + C++) |
| `getOpenEditors` / `getWorkspaceFolders` / `checkDocumentDirty` / `saveDocument` | ✅ real (RDT-backed) |
| `close_tab` / `closeAllDiffTabs` | ✅ real (DiffRegistry) |
| `executeCode` | ✅ MCP error (no VS equivalent) |
| `selection_changed` notification | ✅ real - 150 ms debounce |

- [x] Phase 0 - spike: protocol verified end-to-end vs CLI 2.1.169
- [x] Phase 1 - core 4 in VSIX
- [x] Phase 2 - full 12-tool parity + single-gate hook + dockable panel
- [ ] Phase 3 - VS 2022 backfill, Roslyn-precise ranges, reconnect hardening (see `ROADMAP.md`)
- [ ] Phase 4 - embedded chat (deferred)

## Diagnostics

Currently both C# and C++ diagnostics come from the **Error List** (`SVsErrorList -> IVsTaskList`) via `ErrorListReader.cs`. This is a single unified path that serves both languages - Roslyn pushes C# diagnostics into the Error List and the MSVC toolchain pushes C++ ones. Ranges are point ranges only (the Error List exposes one line/column per entry).

Roslyn-precise C# span ranges (`VisualStudioWorkspace -> Compilation.GetDiagnostics()`) are a Phase 3 enhancement - see `ROADMAP.md`. Always return `[{uri, diagnostics: []}]` - the envelope, even when empty. Requires a loaded project (the Error List is empty for loose files).

## Build / run / test

```powershell
# Extension (Release)
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Rebuild /p:Configuration=Release

# Extension (Debug, then F5 in VS to launch the Experimental instance)
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Rebuild /p:Configuration=Debug

# Spike (Phase 0) - fastest protocol loop; no VS needed
dotnet run --project spike
#   then: claude (with ENABLE_IDE_INTEGRATION + CLAUDE_CODE_SSE_PORT set) -> /ide
```

Protocol smoke test on every CLI bump (the contract is undocumented and has regressed before):
```powershell
claude --version            # record the known-good version
# launch spike, connect claude, confirm: lists mcp__ide__* tools,
# openDiff fires on an edit, accept/reject controls the outcome,
# /permission endpoint responds to a POST with the auth token.
```

## Gotchas

- **`Sec-WebSocket-Protocol: mcp` must be echoed** in the WS upgrade response. Without it the CLI connects (auth OK) then silently drops before `initialize` - looks like a mysterious disconnect. Undocumented; spike-confirmed vs CLI 2.1.169.
- Contract is **undocumented and version-fragile** - pin `claude --version`, smoke-test on every bump.
- `runningInWindows: true` changes how the CLI checks PID liveness (`tasklist.exe` vs `ps`).
- `new_file_contents` in `openDiff` is in-memory -> write to a temp file to feed the comparison; write to `new_file_path` only on Accept.
- Debounce `selection_changed` (~100–200ms) or you'll flood the socket.
