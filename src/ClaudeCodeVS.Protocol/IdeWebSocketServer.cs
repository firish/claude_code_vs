using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// Localhost-only WebSocket server (HttpListener) that speaks MCP to the CLI. Auth is validated
/// during the HTTP upgrade - before the socket opens - so unauthorized clients never get a socket.
/// The receive loop runs off the UI thread; tool handlers that touch VS must marshal to the main
/// thread themselves. See build-plan.md §3 and CLAUDE.md "Non-negotiable conventions" #1, #2.
/// </summary>
public sealed class IdeWebSocketServer
{
    private const string AuthHeader = "x-claude-code-ide-authorization";

    private readonly int _port;
    private readonly string _authToken;
    private readonly McpServer _mcp;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();

    /// <summary>Raised when a CLI client connects (true) or disconnects (false = no clients remain).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Raised when a PULL MCP request (/mcp or /mcp-semantic) is handled - proof the CLI actually loaded
    /// our MCP servers (the stdio shim only POSTs here once it's been spawned + handshook at session
    /// start). BridgeHost uses this to detect the "IDE WebSocket connected but the vs-debug/vs-semantic/
    /// test tools never loaded" gap (Claude launched outside the workspace, or project servers unapproved)
    /// and surface it on the panel instead of failing silently.
    /// </summary>
    public event Action? McpActivity;

    /// <summary>
    /// Raised on any hook-endpoint POST (/permission, /usage, /notify, /debug-context). Hook traffic
    /// arriving while the IDE WebSocket has NEVER connected is the fingerprint of a Claude session
    /// launched outside the extension (workspace hooks loaded, IDE channel never dialed) - the VSIX
    /// uses it to tell the user to run /ide or relaunch from the panel instead of staying silent.
    /// </summary>
    public event Action? HookActivity;

    /// <summary>
    /// Handles a POST /permission request from the PreToolUse hook: given (filePath, proposed new
    /// contents), show a review diff and return whether to allow the edit (+ an optional reject reason
    /// to feed back to the CLI). Set by the VSIX; null means no handler (fail-open). This is how
    /// single-gate works - the hook gates the edit through our diff.
    /// </summary>
    /// <remarks>The third string is the CLI's own permission mode from the hook payload
    /// ("default" | "plan" | "acceptEdits" | "auto" | "dontAsk" | "bypassPermissions"; null on older
    /// CLIs) - the handler allows without a diff for the pre-approving ones (issues #17, #38).</remarks>
    public Func<string, string, string?, CancellationToken, Task<(bool allow, string? reason)>>? PermissionHandler { get; set; }

    /// <summary>
    /// Handles a POST /usage request from the Stop hook: given the conversation transcript path, parse
    /// it and refresh session token/cost stats for the panel. Set by the VSIX; observe-only (the hook
    /// doesn't act on the reply).
    /// </summary>
    public Func<string, CancellationToken, Task>? UsageHandler { get; set; }

    /// <summary>
    /// Raised when the Stop hook POSTs /usage - i.e. Claude finished a turn. The usage refresh is the
    /// payload; this event is the *signal* (BridgeHost raises the "Claude finished" notification from
    /// it). Fired before the usage parse so a slow transcript read never delays the notification.
    /// </summary>
    public event Action? StopReceived;

    /// <summary>
    /// Handles a POST /notify request from the Notification hook: the CLI needs the user's attention
    /// (a permission prompt in the terminal, or Claude went idle waiting for input). The string is the
    /// CLI's human-readable message. Set by the VSIX; observe-only (the hook doesn't act on the reply).
    /// </summary>
    public Func<string, CancellationToken, Task>? NotifyHandler { get; set; }

    /// <summary>
    /// Handles a POST /debug-context request from the UserPromptSubmit hook: read the current VS
    /// debugger state (break location, call stack, locals) and return it as JSON for the hook to inject
    /// into Claude's context. Set by the VSIX; null returns "unknown". This is how debug awareness
    /// reaches the model WITHOUT a tool call or an edit - the hook pushes it in at prompt-submit time.
    /// </summary>
    public Func<CancellationToken, Task<string>>? DebugContextHandler { get; set; }

    /// <summary>
    /// Secondary MCP surface for the Phase 2 debug PULL channel (POST /mcp). The CLI launches a tiny
    /// stdio shim as a normal MCP server; the shim forwards each JSON-RPC message here over HTTP, so the
    /// model can call vs_debug_state / vs_list_breakpoints / vs_get_frame_locals / vs_evaluate on demand.
    /// This is a SEPARATE McpServer (its own tool registry) from the IDE-protocol one on the WebSocket -
    /// the CLI keeps the WebSocket's tools dormant, but treats /mcp's tools as a real, callable server.
    /// Null until the VSIX wires it; a request then returns an empty 200 (no tools).
    /// </summary>
    public McpServer? DebugMcp { get; set; }

    /// <summary>
    /// Third MCP surface (POST /mcp-semantic) for the vs-semantic PULL channel: Roslyn code-navigation
    /// tools (vs_search_symbols / vs_find_references / vs_go_to_definition / vs_find_implementations /
    /// vs_call_hierarchy / vs_type_hierarchy). Same shim, a different -Route, its own tool registry — the
    /// static-analysis sibling of <see cref="DebugMcp"/>'s runtime tools. Null until the VSIX wires it.
    /// </summary>
    public McpServer? SemanticMcp { get; set; }

    public IdeWebSocketServer(int port, string authToken, McpServer mcp)
    {
        _port = port;
        _authToken = authToken;
        _mcp = mcp;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        Log.Info($"WS server listening on ws://127.0.0.1:{_port}/");

        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break; // listener stopped during shutdown
            }
            catch (HttpListenerException e)
            {
                Log.Warn($"listener error: {e.Message}");
                break;
            }

            // Handle each connection independently so one slow client can't block accepts.
            _ = Task.Run(() => HandleContextAsync(ctx, ct), ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";

        // 1) Auth at the HTTP upgrade - reject before any socket is created. Never log the token.
        var presented = ctx.Request.Headers[AuthHeader];
        if (!string.Equals(presented, _authToken, StringComparison.Ordinal))
        {
            Log.Warn($"401 rejected upgrade from {remote} ({(presented is null ? "no" : "bad")} auth token)");
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        // 2) Plain HTTP hook endpoints (PreToolUse /permission, Stop /usage, Notification /notify,
        //    UserPromptSubmit /debug-context), all behind ONE session-ownership gate, then the MCP routes.
        if (!ctx.Request.IsWebSocketRequest)
        {
            var path = ctx.Request.Url?.AbsolutePath;
            bool isPost = string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);
            if (isPost && path is "/permission" or "/usage" or "/notify" or "/debug-context")
            {
                // Read + parse the body ONCE here: the ownership gate needs the hook's cwd, and the
                // request stream cannot be re-read by the handlers.
                string rawBody;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    rawBody = await reader.ReadToEndAsync();
                JObject body;
                try { body = JObject.Parse(rawBody); }
                catch { body = new JObject(); } // malformed -> fail-open, handlers see empty fields

                // Session-ownership gate (PR #28 rework): the hooks route by most-specific lockfile
                // match but fall back to ANY listening VS bridge when no workspace matches the
                // session's cwd - so another workspace's session can land its POSTs here. The hook now
                // sends its cwd; a POST whose cwd is outside this bridge's workspace is answered
                // benignly and IGNORED, so foreign sessions can't raise notifications, open diffs,
                // pollute token stats, read this instance's debugger state, or trip the hooks-only
                // banner. Missing cwd (older/user-owned script) or no open workspace -> fail-open.
                if (!IsOwnSession(body, out string foreignCwd))
                {
                    Log.Info($"hook POST {path} ignored: session cwd '{foreignCwd}' is outside this workspace");
                    await RespondForeignAsync(ctx, path!, ct);
                    return;
                }

                HookActivity?.Invoke(); // after the gate: foreign traffic must not look like a local session

                // Refresh the session's CLI permission mode from ANY hook that carries it, not just
                // /permission. permission_mode is common to every hook payload, but only the PreToolUse
                // hook fires on an edit - so a session that switched out of a pre-approving mode and then
                // did no edits left the panel's run-wild checkbox stuck checked AND disabled, with no way
                // for the user to clear it. Every prompt (/debug-context) and every turn end (/usage) now
                // re-samples it. Only ever SET from here: a script too old to send the field must not be
                // read as "the mode was cleared" - /permission owns that, it always knows.
                var observedMode = (string?)body["permissionMode"];
                if (!string.IsNullOrEmpty(observedMode))
                {
                    try { PermissionModeObserved?.Invoke(observedMode); }
                    catch (Exception e) { Log.Warn($"permission-mode observer failed: {e.Message}"); }
                }

                switch (path)
                {
                    case "/permission": await HandlePermissionRequestAsync(ctx, body, ct); return;
                    case "/usage": await HandleUsageRequestAsync(ctx, body, ct); return;
                    case "/notify": await HandleNotifyRequestAsync(ctx, body, ct); return;
                    default: await HandleDebugContextRequestAsync(ctx, ct); return;
                }
            }
            if (isPost
                && ctx.Request.Url?.AbsolutePath == "/mcp")
            {
                await HandleMcpRequestAsync(ctx, DebugMcp, "/mcp", ct);
                return;
            }
            if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                && ctx.Request.Url?.AbsolutePath == "/mcp-semantic")
            {
                await HandleMcpRequestAsync(ctx, SemanticMcp, "/mcp-semantic", ct);
                return;
            }
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        // 3) Accept the socket. THE CRITICAL HANDSHAKE DETAIL (CLAUDE.md gotcha): the CLI sends
        // `Sec-WebSocket-Protocol: mcp` and we MUST echo it in the 101 response, or the CLI connects
        // then silently drops before `initialize`. AcceptWebSocketAsync throws if we name a
        // subprotocol the client didn't offer, so only pass through what was actually requested.
        string? subprotocol = FirstRequestedSubprotocol(ctx.Request);
        WebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: subprotocol);
        }
        catch (Exception e)
        {
            Log.Error($"WS accept failed from {remote}: {e.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }
        if (subprotocol is not null)
            Log.Info($"negotiated subprotocol: '{subprotocol}'");

        var conn = new Connection(wsCtx.WebSocket);
        _connections[conn] = 0;
        Log.Info($"client connected from {remote} (authorized)");
        try { ConnectionChanged?.Invoke(true); } catch { }

        try
        {
            await ReceiveLoopAsync(conn, ct);
        }
        finally
        {
            _connections.TryRemove(conn, out _);
            conn.Dispose();
            Log.Info($"client disconnected ({remote})");
            try { ConnectionChanged?.Invoke(HasConnections); } catch { }
        }
    }

    /// <summary>
    /// The bridge's workspace root, for the session-ownership gate ahead of the hook endpoints.
    /// Null/empty (or unset) disables the gate - a bridge with no folder open can't discriminate.
    /// A provider (not a snapshot) because the workspace can load after the server starts.
    /// </summary>
    public Func<string?>? WorkspaceProvider { get; set; }

    /// <summary>
    /// Raised with the session's CLI permission mode whenever an owned hook POST carries one, so the
    /// panel tracks mode changes made with shift+tab instead of only learning about them on the next edit.
    /// </summary>
    public Action<string>? PermissionModeObserved { get; set; }

    /// <summary>
    /// True when the POSTing session belongs to this bridge's workspace: body.cwd equals the
    /// workspace root or sits beneath it (separator-aware, case-insensitive, / == \). Fail-open on
    /// a missing cwd (older or user-owned hook script) or no open workspace.
    /// </summary>
    private bool IsOwnSession(JObject body, out string cwd)
    {
        cwd = (string?)body["cwd"] ?? "";
        var ws = WorkspaceProvider?.Invoke();
        if (string.IsNullOrEmpty(ws)) return true;

        string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
        string w = Norm(ws!);
        bool Covers(string outer, string inner) =>
            inner.Equals(outer, StringComparison.OrdinalIgnoreCase) ||
            inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);

        // The FILE is the most direct ownership signal there is: if this VS has the edited file's folder
        // open, it can review that edit, whatever the session's cwd happens to be. /permission carries it.
        var file = (string?)body["filePath"] ?? "";
        if (file.Length > 0 && Covers(w, Norm(file))) return true;

        if (cwd.Length == 0) return true; // older or user-owned hook script sends no cwd -> fail open
        string c = Norm(cwd);

        // Containment counts in BOTH directions. A session rooted at a PARENT of the workspace - open
        // `demo\BuildBreak.slnx` in VS, run `claude` from `demo\` - legitimately covers that workspace.
        // Treating it as foreign was the bug: /permission answered ask=true, the CLI fell back to its own
        // permission prompt, and because it is IDE-connected it rendered that prompt as an openDiff. The
        // user then saw a diff the panel's auto-accept toggle had no say over (the gate refuses before
        // auto-accept is ever consulted) plus a terminal prompt that its Accept did not answer.
        // Genuinely disjoint trees (C:\work\app vs C:\work\app-service) still match neither direction and
        // stay refused, which is the multi-instance case the gate exists for.
        return Covers(w, c) || Covers(c, w);
    }

    /// <summary>
    /// Benign answer for a foreign session's hook POST. /permission gets ask=true - the hook hands
    /// the decision back to the CLI's own permission prompt (NEVER auto-allow a session this VS
    /// can't review); /debug-context gets the inject-nothing envelope; the observe-only endpoints
    /// get an empty 200. Always 200: the hooks are fail-open and an error would read as a fault.
    /// </summary>
    private static async Task RespondForeignAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        try
        {
            string json = path switch
            {
                "/permission" => new JObject
                {
                    ["allow"] = false,
                    ["ask"] = true,
                    ["reason"] = "Session folder is outside this Visual Studio's workspace.",
                }.ToString(Formatting.None),
                "/debug-context" => "{\"mode\":\"unknown\"}",
                _ => "{}",
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8"; // no charset -> PS 5.1 clients decode as Latin-1
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    private async Task HandlePermissionRequestAsync(HttpListenerContext ctx, JObject o, CancellationToken ct)
    {
        bool allow = true; // fail-open: never block the CLI because our review path errored
        string? reason = null;
        try
        {
            var filePath = (string?)o["filePath"] ?? "";
            var newContents = (string?)o["newContents"] ?? "";
            var transcript = (string?)o["transcript_path"];
            var permissionMode = (string?)o["permissionMode"];
            Log.Info($"permission request: {filePath} ({newContents.Length} chars{(string.IsNullOrEmpty(permissionMode) ? "" : $", CLI mode {permissionMode}")})");

            // Refresh token/cost stats from the transcript on each edit. The Stop hook also does this,
            // but the permission hook is the reliable trigger. Fire-and-forget so the diff isn't delayed.
            if (!string.IsNullOrEmpty(transcript) && UsageHandler is { } uh)
                _ = uh(transcript!, ct);

            var handler = PermissionHandler;
            if (handler != null && filePath.Length > 0)
                (allow, reason) = await handler(filePath, newContents, permissionMode, ct);
            Log.Info($"permission decision: {(allow ? "allow" : "deny")} for {filePath}");
        }
        catch (Exception e)
        {
            Log.Warn($"permission request failed (allowing): {e.Message}");
            allow = true;
        }

        try
        {
            var json = new JObject { ["allow"] = allow, ["reason"] = reason }.ToString(Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8"; // no charset -> PS 5.1 clients decode as Latin-1 (mojibake)
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    private async Task HandleUsageRequestAsync(HttpListenerContext ctx, JObject o, CancellationToken ct)
    {
        // The Stop hook is the only /usage caller, so a (workspace-owned) hit here means the turn
        // ended. Signal first - the transcript parse below can be slow and the notification
        // shouldn't wait on it.
        try { StopReceived?.Invoke(); } catch { }
        try
        {
            var transcript = (string?)o["transcript_path"] ?? "";
            var handler = UsageHandler;
            if (handler != null && transcript.Length > 0)
            {
                // Fire-and-forget: the hook is observe-only (it never reads the reply), but it DOES wait
                // for the response - holding it open while a long transcript parses can blow the CLI's
                // hook timeout. Respond immediately; parse in the background.
                _ = Task.Run(async () =>
                {
                    try { await handler(transcript, ct); }
                    catch (Exception e) { Log.Warn($"usage transcript parse failed: {e.Message}"); }
                }, ct);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"usage request failed: {e.Message}");
        }
        try { ctx.Response.StatusCode = 200; ctx.Response.Close(); } catch { /* client gave up */ }
    }

    private async Task HandleNotifyRequestAsync(HttpListenerContext ctx, JObject o, CancellationToken ct)
    {
        try
        {
            var message = (string?)o["message"] ?? "";
            var handler = NotifyHandler;
            if (handler != null && message.Length > 0)
                await handler(message, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"notify request failed: {e.Message}");
        }
        try { ctx.Response.StatusCode = 200; ctx.Response.Close(); } catch { /* client gave up */ }
    }

    private async Task HandleDebugContextRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string json = "{\"mode\":\"unknown\"}"; // fail-safe: the hook injects nothing on "unknown"
        try
        {
            // The body (cwd) was consumed by the dispatch-level ownership gate; the handler itself
            // just reads this VS instance's own debugger.
            var handler = DebugContextHandler;
            if (handler != null)
                json = await handler(ct) ?? json;
        }
        catch (Exception e)
        {
            Log.Warn($"debug-context request failed: {e.Message}");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8"; // no charset -> PS 5.1 clients decode as Latin-1 (mojibake)
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    /// <summary>
    /// POST /mcp - the Phase 2 debug PULL surface. One JSON-RPC message in the body, one JSON-RPC message
    /// back (or an empty 200 for notifications, where HandleAsync returns null). The stdio shim does the
    /// newline framing; here it's one request per POST. Auth was already validated at the upgrade above.
    /// </summary>
    private async Task HandleMcpRequestAsync(HttpListenerContext ctx, McpServer? mcp, string routePath, CancellationToken ct)
    {
        // Any hit here proves the CLI loaded our MCP servers (the shim only reaches this route once it's
        // been spawned and handshook). Signal it so the "tools didn't load" watcher can stand down.
        try { McpActivity?.Invoke(); } catch { }

        string? response = null;
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            if (mcp != null && body.Length > 0)
                response = await mcp.HandleAsync(body, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"{routePath} request failed: {e.Message}");
        }

        try
        {
            ctx.Response.StatusCode = 200;
            if (response is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json; charset=utf-8"; // no charset -> PS 5.1 clients decode as Latin-1 (mojibake)
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            }
            else
            {
                ctx.Response.ContentLength64 = 0; // notification / no reply due
            }
            ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    private async Task ReceiveLoopAsync(Connection conn, CancellationToken ct)
    {
        var ws = conn.Socket;
        var buffer = new byte[8192];
        var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException e)
            {
                Log.Warn($"receive error: {e.Message}");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                break;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue; // keep accumulating a fragmented frame

            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            Log.Frame(inbound: true, json);

            // Dispatch off the receive loop so a deferred tool call (openDiff blocks until the user
            // decides) doesn't stall reading subsequent frames.
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await _mcp.HandleAsync(json, ct);
                    if (response is not null)
                        await conn.SendAsync(response, ct);
                }
                catch (Exception e)
                {
                    Log.Error($"dispatch error: {e.Message}");
                }
            }, ct);
        }
    }

    private static string? FirstRequestedSubprotocol(HttpListenerRequest req)
    {
        var raw = req.Headers["Sec-WebSocket-Protocol"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // net48 has no StringSplitOptions.TrimEntries (.NET 5+), so trim explicitly.
        var first = raw!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return first.Length == 0 ? null : first;
    }

    /// <summary>Push a JSON-RPC notification (no id) to every connected client, e.g. selection_changed.</summary>
    public async Task BroadcastNotificationAsync(string method, JToken @params, CancellationToken ct)
    {
        var frame = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        }.ToString(Formatting.None);

        foreach (var conn in _connections.Keys)
            await conn.SendAsync(frame, ct);
    }

    /// <summary>True when at least one CLI client is connected (gates selection_changed pushes).</summary>
    public bool HasConnections => !_connections.IsEmpty;

    /// <summary>One client connection. Serializes sends (WebSocket.SendAsync isn't concurrency-safe).</summary>
    private sealed class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public WebSocket Socket { get; } = socket;

        public async Task SendAsync(string json, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync(ct);
            try
            {
                Log.Frame(inbound: false, json);
                var bytes = Encoding.UTF8.GetBytes(json);
                await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            Socket.Dispose();
        }
    }
}
