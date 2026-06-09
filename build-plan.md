# ClaudeCodeVS — Build Plan

> Working title; rename freely. A native Visual Studio 2026 extension that launches the real `claude` CLI and implements Claude Code's IDE-integration protocol, so you get a **native VS diff window with accept/reject** plus **automatic selection + diagnostics context** — while inheriting every Claude Code feature (skills, plugins, hooks, MCP, subagents, checkpoints, compaction, Pro/Max auth) for free, because the real CLI is doing the agent work.

---

## 1. The wedge (why this exists)

Three approaches exist in the wild for putting Claude Code in Visual Studio:

| Approach | Example | Inherits Claude Code features | Pro/Max auth | Real interactive diff | Auto context | Main cost |
|---|---|---|---|---|---|---|
| A. Embedded terminal host | `dliedke/ClaudeCodeExtension` | ✅ | ✅ | ❌ git-diff view only | ❌ manual paste | terminal plumbing hell |
| B. From-scratch agent | `adospace/vs-agentic` | ❌ | ❌ API key, metered | ✅ (its own) | ✅ (its own) | rebuild + forever-lag |
| **C. Protocol bridge (this project)** | *nobody yet* | ✅ | ✅ | ✅ real | ✅ real | tracking an undocumented protocol |

Approach A runs the CLI but ignores the IDE protocol (its diff is a passive `git diff` of what Claude already wrote). Approach B reimplements the whole agent against the raw API and loses the entire product surface. **C is the only unfilled combination**, and it is exactly what Anthropic feature request [#15942](https://github.com/anthropics/claude-code/issues/15942) asks for: a dockable panel, native diff with review/accept/reject, and awareness of open files, selection, and compiler diagnostics.

**Differentiators that make people switch (priority order):**
1. Real interactive diff with accept/reject in VS's native diff viewer — the #1 stated pain.
2. Automatic context: active file, selection, and especially the **Error List / build diagnostics** (the native-VS advantage; huge for the C++/.NET crowd).
3. No terminal fragility.

Skills/plugins/hooks/MCP are **not built** — they come for free by riding the CLI. That's the entire point of C.

---

## 2. Architecture

Four pieces, all inside one in-proc VSIX:

1. **Lockfile writer** — announces "an IDE is here" to the CLI.
2. **WebSocket server** — localhost-only, MCP/JSON-RPC 2.0, auth-gated.
3. **Tool handlers** — bridge MCP tool calls to VS SDK services (the real work).
4. **CLI launcher** — starts `claude` with the right env so it auto-connects.

**Data flow:**
```
extension picks free port
  -> writes ~/.claude/ide/<port>.lock   (filename == port)
  -> starts WS server on 127.0.0.1:<port>  (validates auth header on upgrade)
  -> launches `claude` with CLAUDE_CODE_SSE_PORT=<port> ENABLE_IDE_INTEGRATION=true
  -> CLI reads lockfile, finds port from env, connects, sends auth header
  -> MCP handshake (initialize / tools/list)
  -> from now on: every edit Claude proposes arrives as an openDiff tool call
                  every selection you make is pushed as a selection_changed notification
```

---

## 3. The protocol contract (verified)

### Lockfile

Path: `~/.claude/ide/<port>.lock` (Windows: `C:\Users\<you>\.claude\ide\<port>.lock`).
**The filename is the port the WS server listens on.**

```json
{
  "pid": 12345,
  "workspaceFolders": ["C:\\path\\to\\solution"],
  "ideName": "Visual Studio",
  "transport": "ws",
  "runningInWindows": true,
  "authToken": "550e8400-e29b-41d4-a716-446655440000"
}
```

- `authToken` — random UUID generated at startup.
- `runningInWindows` — set `true` for VS-on-Windows; the CLI uses it to verify PID liveness via `tasklist.exe` instead of `ps` (matters in WSL scenarios).
- `workspaceFolders` — solution root(s). Open-Folder mode: the folder root.

### Environment (set before launching the CLI)

```
CLAUDE_CODE_SSE_PORT=<port>
ENABLE_IDE_INTEGRATION=true
```

### Auth handshake

The CLI connects to `ws://127.0.0.1:<port>/` and sends a custom header:

```
x-claude-code-ide-authorization: <authToken>
```

The server validates this against the lockfile's `authToken` and **rejects on mismatch**. Validate during the HTTP upgrade — with `HttpListener` you can read `request.Headers` before calling `AcceptWebSocketAsync`, so unauthorized upgrades are rejected with 401 before the socket opens.

- **Bind to 127.0.0.1 only.** Never expose this.
- Connection attempt times out at ~35s.

### WebSocket subprotocol (VERIFIED — spike, CLI 2.1.169)

The CLI's upgrade request includes **`Sec-WebSocket-Protocol: mcp`**, and the server **must echo it back** in the 101 response. If it doesn't, the CLI completes the TCP/WS connect (auth passes) and then **immediately closes the socket before sending `initialize`** — no error, just a silent drop. This cost us the first real-CLI test and is documented nowhere; it's the single most important handshake detail. In .NET `HttpListener`, pass the requested value to `AcceptWebSocketAsync(subProtocol)` rather than `null`. The CLI also offers `Sec-WebSocket-Extensions: permessage-deflate`; we don't negotiate it and the connection is fine.

### MCP wire protocol

Standard MCP over the socket (JSON-RPC 2.0):
- `initialize` → return `serverInfo`, `protocolVersion`, `capabilities`. VERIFIED: CLI 2.1.169 sends `protocolVersion: "2025-11-25"`, client capabilities `{roots:{}, elicitation:{}}`, and uses `id: 0` for this first request — **echo the client's `protocolVersion` back** rather than hardcoding.
- `tools/list` → advertise tool schemas (§4). These become the `mcp__ide__*` names the model sees.
- `tools/call` → dispatch by tool name. VERIFIED: params carry `_meta: {progressToken}` (ignore it). **Diff lifecycle:** edit → `openDiff` (blocks) → `DIFF_ACCEPTED` → the CLI then calls **`close_tab {tab_name}`** to close the view; and on connect it calls **`closeAllDiffTabs`**. So both close tools are part of the *core* diff flow, not optional parity — stub them as success no-ops in Phase 1 (the spike returns isError; the CLI tolerates it). `tab_name` is a decorated opaque string like `✻ [Claude Code] Log.cs (c7fbef) ⧉` — key on it, don't parse it.
- Notifications (no `id`) flow both ways; we push `selection_changed`. VERIFIED inbound: after `initialize`, the CLI sends `notifications/initialized` then a custom **`ide_connected` notification** with `params {pid}` (the CLI's PID; not one of the 12 tools).

**Return-value wire quirk (copy from the Nova port):** plain-string returns flow through verbatim (`"TAB_CLOSED"`, `"FILE_SAVED"`, `"DIFF_ACCEPTED"`, `"DIFF_REJECTED"`); objects are JSON-stringify-wrapped; errors surface via the MCP `isError` flag.

---

## 4. Tool schemas (`tools/list` payload)

The **core 4** (+ the `selection_changed` notification) are the 90%. The rest are for full parity and can ship later.

```jsonc
// ---- CORE 4 ----

// openFile: open a file, optionally navigate/select a range
{
  "name": "openFile",
  "description": "Open a file in the editor and optionally select a range.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "filePath":     { "type": "string" },
      "preview":      { "type": "boolean", "default": false },
      "startLine":    { "type": "integer" },
      "endLine":      { "type": "integer" },
      "startText":    { "type": "string" },
      "endText":      { "type": "string" },
      "makeFrontmost":{ "type": "boolean", "default": true }
    },
    "required": ["filePath"]
  }
}

// openDiff: show a diff and BLOCK until the user accepts/rejects (deferred response)
{
  "name": "openDiff",
  "description": "Show a diff between an on-disk file and proposed new contents; wait for the user to accept or reject.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "old_file_path":     { "type": "string" },
      "new_file_path":     { "type": "string" },
      "new_file_contents": { "type": "string" },
      "tab_name":          { "type": "string" }
    },
    "required": ["old_file_path", "new_file_path", "new_file_contents", "tab_name"]
  }
}
// Returns "DIFF_ACCEPTED" (+ optionally final contents) or "DIFF_REJECTED".
// Stats computed against old_file_path; new_file_contents written to new_file_path on accept.

// getCurrentSelection: current selection in the active editor
{
  "name": "getCurrentSelection",
  "description": "Get the current text selection in the active editor.",
  "inputSchema": { "type": "object", "properties": {} }
}

// getDiagnostics: LSP-shaped diagnostics, optionally filtered by file uri
{
  "name": "getDiagnostics",
  "description": "Get language/build diagnostics. Returns [{uri, diagnostics:[...]}] (empty array if none).",
  "inputSchema": {
    "type": "object",
    "properties": { "uri": { "type": "string" } }
  }
}
```

```jsonc
// ---- REMAINING 8 (full parity; stub now, implement in Phase 2) ----
{ "name": "getLatestSelection",  "inputSchema": { "type": "object", "properties": {} } },
{ "name": "getOpenEditors",      "inputSchema": { "type": "object", "properties": {} } },
{ "name": "getWorkspaceFolders", "inputSchema": { "type": "object", "properties": {} } },
{ "name": "checkDocumentDirty",  "inputSchema": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
{ "name": "saveDocument",        "inputSchema": { "type": "object", "properties": { "filePath": { "type": "string" } }, "required": ["filePath"] } },
{ "name": "close_tab",           "inputSchema": { "type": "object", "properties": { "tab_name": { "type": "string" } }, "required": ["tab_name"] } },
{ "name": "closeAllDiffTabs",    "inputSchema": { "type": "object", "properties": {} } },
{ "name": "executeCode",         "inputSchema": { "type": "object", "properties": { "code": { "type": "string" } }, "required": ["code"] } }
// executeCode = Jupyter kernel execution; no VS equivalent -> return an honest MCP error.
```

### `selection_changed` notification (push, not a tool)

Subscribe to the active editor's selection-changed event, debounce, and push:

```json
{
  "jsonrpc": "2.0",
  "method": "selection_changed",
  "params": {
    "text": "...selected text...",
    "filePath": "C:\\path\\to\\File.cs",
    "fileUrl": "file:///C:/path/to/File.cs",
    "selection": {
      "start": { "line": 10, "character": 4 },
      "end":   { "line": 12, "character": 0 }
    }
  }
}
```

---

## 5. Tool → VS-native API mapping

### `openDiff` (the centerpiece — 70% of the value)
- **Render:** `IVsDifferenceService.OpenComparisonWindow2`. `new_file_contents` is in-memory → write to a temp file and diff `old_file_path` (left) vs temp (right). For an editable right pane, host `IWpfDifferenceViewerFactoryService.CreateDifferenceView` over an `IDifferenceBuffer` (from `IDifferenceBufferFactoryService`) in your own tool window.
- **Accept/Reject chrome:** the native diff window is a *viewer* with no buttons — add a VS `InfoBar` across the top ("Claude proposes this change: [Accept] [Reject]") or a tool-window toolbar.
- **Deferred response (do not get this wrong):** on `tools/call`, marshal to the UI thread, open the diff, and park the JSON-RPC reply on a `TaskCompletionSource<string>` keyed by `tab_name`. Accept/Reject handlers complete the TCS. On Accept → write `new_file_contents` to `new_file_path` via the RDT, save, complete with `"DIFF_ACCEPTED"`. On Reject → complete with `"DIFF_REJECTED"`. Either way, close the diff frame.

### `getCurrentSelection` / `getLatestSelection`
- `IVsTextManager2.GetActiveView2` → `IVsTextView`; convert via `IVsEditorAdaptersFactoryService.GetWpfTextView()` → `IWpfTextView.Selection` for the `SnapshotSpan`. File path from the buffer's `ITextDocument` (via `ITextDocumentFactoryService`). `getLatestSelection` returns a cached last-non-empty value.

### `getDiagnostics`
- **C#/.NET (precise ranges):** MEF-import `VisualStudioWorkspace`; per `Document` pull `Compilation.GetDiagnostics()`; map `Diagnostic.Location.GetLineSpan()` → LSP range, `DiagnosticSeverity` → LSP severity.
- **C++ (no Roslyn — the #15942 audience):** fall back to the Error List (`SVsErrorList` / `ErrorListProvider`). Coarser ranges; make this mapping solid, not an afterthought.
- Always return the envelope `[{uri, diagnostics: []}]` even when empty.

### `openFile`
- `VsShellUtilities.OpenDocument(...)` → grab the `IVsTextView` → `SetSelection` + `EnsureSpanVisible` (or `IVsTextManager.NavigateToLineAndColumn`) to jump to a line.

### `selection_changed` (notification)
- Subscribe to `IWpfTextView.Selection.SelectionChanged` (+ active-view-changed), debounce (~100–200ms), push over the socket. Half the "context awareness" win, and cheap.

### Remaining (trivial; Phase 2)
- `getOpenEditors` / `checkDocumentDirty` / `saveDocument` → `IVsRunningDocumentTable`.
- `getWorkspaceFolders` → `IVsSolution.GetSolutionInfo` (Open-Folder: workspace root).
- `close_tab` / `closeAllDiffTabs` → `IVsWindowFrame.CloseFrame` on tracked diff frames.
- `executeCode` → return an MCP error (no VS equivalent).

---

## 6. C# handler scaffolding (stub)

```csharp
// All editor-touching code MUST run on the UI thread:
//   await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

public interface IIdeTool
{
    string Name { get; }
    // Returns either a plain string (sent verbatim) or an object (JSON-wrapped).
    Task<object> InvokeAsync(JsonElement args, CancellationToken ct);
}

// Dispatch (in the WS message loop, off the UI thread):
public async Task<object> DispatchAsync(string name, JsonElement args, CancellationToken ct)
{
    if (!_tools.TryGetValue(name, out var tool))
        throw new McpException($"unknown tool: {name}"); // -> isError
    return await tool.InvokeAsync(args, ct);
}

// openDiff with the deferred pattern:
public sealed class OpenDiffTool : IIdeTool
{
    public string Name => "openDiff";

    // tab_name -> pending decision
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public async Task<object> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var oldPath  = args.GetProperty("old_file_path").GetString();
        var newPath  = args.GetProperty("new_file_path").GetString();
        var contents = args.GetProperty("new_file_contents").GetString();
        var tabName  = args.GetProperty("tab_name").GetString();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tabName] = tcs;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        // 1. write `contents` to a temp file
        // 2. IVsDifferenceService.OpenComparisonWindow2(oldPath, tempPath, caption: tabName, ...)
        // 3. attach an InfoBar with [Accept]/[Reject] wired to OnDecision(tabName, accepted)

        // BLOCK until the user decides (this is what makes Claude wait):
        return await tcs.Task; // "DIFF_ACCEPTED" or "DIFF_REJECTED"
    }

    public void OnDecision(string tabName, bool accepted)
    {
        if (_pending.TryRemove(tabName, out var tcs))
            tcs.TrySetResult(accepted ? "DIFF_ACCEPTED" : "DIFF_REJECTED");
        // on accept: write contents -> newPath via RDT, save; then close the diff frame
    }
}
```

---

## 7. Build sequence

### Phase 0 — Protocol spike (standalone console, **no VSIX**). De-risks everything.
**Goal:** prove the undocumented contract end-to-end before touching the VS SDK. A `net8.0` console app is fine here (HttpListener + System.Net.WebSockets behave the same; the real extension will be `net48` in-proc).

| # | Task | Acceptance criteria |
|---|---|---|
| 0.1 | Console skeleton + structured logging | Runs; logs every inbound/outbound JSON-RPC frame. |
| 0.2 | Free-port pick + lockfile writer (exact §3 schema, UUID token) | `~/.claude/ide/<port>.lock` appears with valid JSON and correct fields; filename == listening port. |
| 0.3 | `HttpListener` WS server on `127.0.0.1:<port>` + auth | Client with correct `x-claude-code-ide-authorization` connects; wrong/missing token → 401, no socket. |
| 0.4 | MCP handshake: `initialize` + `tools/list` (core-4 schemas) | Launch `claude` with env set, run `/ide` → shows connected; `mcp__ide__*` tools appear in-session. |
| 0.5 | Stub `getCurrentSelection` (hardcoded) + `openDiff` (deferred) | Ask Claude to edit a file → `openDiff` fires with the 4 params; a console keypress completes the deferred TCS; `DIFF_ACCEPTED` lets Claude proceed, `DIFF_REJECTED` makes it back off. |
| 0.6 | Env launcher: spawn `claude` with `CLAUDE_CODE_SSE_PORT` + `ENABLE_IDE_INTEGRATION` | `/ide` auto-connects with no manual port entry. |
| 0.7 | Lockfile lifecycle: delete on exit; clean stale (dead-PID) lockfiles | No orphan lockfiles after a clean exit; stale files from prior crashes are reaped on startup. |

**Phase 0 exit criteria:** from a connected Claude session, a file-edit request round-trips through *your* `openDiff` and *your* accept/reject controls the outcome. Everything after this is replacing stubs with real VS SDK calls.

### Phase 1 — Core 4 in a real VSIX (ship this).
- Convert to in-proc VSIX (`net48`, VSSDK + Community Toolkit).
- Real `openDiff` (IVsDifferenceService + InfoBar accept/reject + write-back), `getCurrentSelection` (+ `getLatestSelection`), `getDiagnostics` (Roslyn for .NET, Error List for C++), `openFile`.
- `selection_changed` notification.
- Lockfile + WS server + auth, in-proc, with strict main-thread marshaling.
- **Delivers exactly what #15942 asks for.**

### Phase 2 — Full 12-tool parity + robustness.
- Remaining tools (RDT / solution / window-frame plumbing; `executeCode` → MCP error).
- Stale-lockfile cleanup tied to WS connection lifetime (known failure: socket goes stale while lockfile persists, blocking reconnect — issue #5043).
- 35s timeout handling, reconnect, multi-solution / multi-window, theme-aware diff chrome.

### Phase 3 — Embedded chat UX (optional, defer).
- Hosting the `claude` terminal inside a VS tool window is where Approach A burned ~150 commits (clipboard/paste/focus/encoding). Until Phase 1 proves adoption, skip it: run the server + set env on a terminal you launch (even external), let chat live there while diffs/context light up natively in VS. **The env vars are the clean handoff — that's the whole trick.**

---

## 8. Decisions locked

- **Extensibility model: in-proc VSSDK + Community Toolkit.** Non-negotiable — `IVsDifferenceService`, the WPF differencing factory, Roslyn workspace, RDT, and editor adapters are all in-proc services. The out-of-process `VisualStudio.Extensibility` model does not expose these, so it cannot host the diff core. Community Toolkit just trims VSSDK boilerplate.
- **Runtime: .NET Framework (`net48`)** for the in-proc extension (consequence of the above).
- **WebSocket: `HttpListener`-based**, `127.0.0.1` only, header auth at upgrade. No external dependency.
- **VS version floor: 2026 only, for now.** 2026 is exactly where #15942 points, and pinning to it alone halves the test matrix so the spike and Phase 1 move fast. 2022 still has the larger install base today, so **backfill it once Phase 1 proves out** — the cost is a VSIX manifest version-range widening plus retesting the in-proc SDK calls (`IVsDifferenceService`, the WPF differencing factory, Roslyn workspace, RDT) against the 2022 SDK. Nothing in the protocol or architecture is 2026-specific, so this is a deferral, not a fork.
- **Threading is the #1 engineering risk.** WS receive loop is background; every editor touch needs `JoinableTaskFactory.SwitchToMainThreadAsync()`. Establish the pattern once, reuse everywhere.

---

## 9. Risks / ongoing maintenance

- **Undocumented, version-fragile contract.** It has broken across CLI releases before (JetBrains v2.1.23 regression #23119; VS Code "no IDE tools" #51358). **Pin a known-good `claude --version` in CI and smoke-test the handshake on every CLI bump.**
- **Thin moat.** Anthropic owns this gap (#15942 is on their tracker) and could ship official VS support and obsolete this overnight. Treat it as a personal/internal productivity tool unless/until traction says otherwise.
- **Incumbent.** `dliedke/ClaudeCodeExtension` is actively maintained with real users tolerating "good enough" terminal + git-diff. Differentiation must be the protocol-level diff/diagnostics they can *feel*, not a longer feature list.
- **C++ diagnostics are weaker than C#** (no Roslyn) — but C++ is the core #15942 audience, so invest in a good Error List mapping for them specifically.

---

## 10. References

Protocol / reverse-engineering:
- `coder/claudecode.nvim` PROTOCOL.md — lockfile schema, auth header, env vars, JSON-RPC framing: https://github.com/coder/claudecode.nvim/blob/main/PROTOCOL.md
- Nova "Claude Code Bridge" (Panic) — full 12-tool list + return-value wire format: https://extensions.panic.com/extensions/ca.okapi/ca.okapi.claudecode-nova/
- `Yuyz0112/claude-code-reverse` — `mcp__ide__*` tool behavior, system-reminder injection: https://github.com/Yuyz0112/claude-code-reverse
- Lockfile/auth field confirmations across bug reports: anthropics/claude-code #5043, #14421, #16434, #22494, #36284.

Demand / competitors:
- Feature request #15942 (native VS 2026 parity): https://github.com/anthropics/claude-code/issues/15942
- `dliedke/ClaudeCodeExtension` (Approach A): https://github.com/dliedke/ClaudeCodeExtension
- `adospace/vs-agentic` (Approach B): https://github.com/adospace/vs-agentic

Visual Studio SDK:
- `IVsDifferenceService` / comparison windows (used by GitHub's VS extension).
- `IWpfDifferenceViewerFactoryService`, `IDifferenceBufferFactoryService`.
- Roslyn `VisualStudioWorkspace`; `IVsRunningDocumentTable`; `IVsSolution`; VSSDK + Community Toolkit threading (`JoinableTaskFactory`).
