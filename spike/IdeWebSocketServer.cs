using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// Localhost-only WebSocket server (HttpListener) that speaks MCP to the CLI. Auth is validated
/// during the HTTP upgrade - before the socket opens - so unauthorized clients never get a socket.
/// The same HttpListener + System.Net.WebSockets code carries over to the net48 in-proc extension.
/// See build-plan.md §3 and CLAUDE.md "Non-negotiable conventions" #2.
/// </summary>
internal sealed class IdeWebSocketServer
{
    private const string AuthHeader = "x-claude-code-ide-authorization";

    private readonly int _port;
    private readonly string _authToken;
    private readonly McpServer _mcp;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();

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

        // DIAGNOSTIC (spike): dump the upgrade request headers so we can see exactly what the real
        // CLI sends - subprotocol, version, origin, etc. The auth token value is redacted.
        LogUpgradeHeaders(ctx.Request);

        // 2) Must actually be a WebSocket upgrade.
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        // 3) Accept the socket. Echo back the client's requested subprotocol if it offered one -
        // AcceptWebSocketAsync throws if we name a subprotocol the client didn't request, so only
        // pass through what was actually offered.
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

        try
        {
            await ReceiveLoopAsync(conn, ct);
        }
        finally
        {
            _connections.TryRemove(conn, out _);
            conn.Dispose();
            Log.Info($"client disconnected ({remote})");
        }
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

    // DIAGNOSTIC helpers (spike) ------------------------------------------------------------

    private static void LogUpgradeHeaders(HttpListenerRequest req)
    {
        var sb = new StringBuilder();
        foreach (var key in req.Headers.AllKeys)
        {
            if (key is null) continue;
            var value = string.Equals(key, AuthHeader, StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : req.Headers[key];
            sb.Append($"\n        {key}: {value}");
        }
        Log.Event($"upgrade request headers:{sb}");
    }

    private static string? FirstRequestedSubprotocol(HttpListenerRequest req)
    {
        var raw = req.Headers["Sec-WebSocket-Protocol"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    /// <summary>Push a JSON-RPC notification (no id) to every connected client, e.g. selection_changed.</summary>
    public async Task BroadcastNotificationAsync(string method, JsonNode @params, CancellationToken ct)
    {
        var frame = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        }.ToJsonString();

        foreach (var conn in _connections.Keys)
            await conn.SendAsync(frame, ct);
    }

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
