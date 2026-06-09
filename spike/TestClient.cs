using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// An automated stand-in for the CLI: it discovers the server via the lockfile, connects with the
/// auth header, and exercises the full MCP surface — asserting the wire shapes along the way.
/// This proves our SERVER is correct without needing an interactive `/ide` session. The real CLI
/// is the separate, authoritative check (see ClaudeLauncher / the manual run hint).
/// </summary>
internal static class TestClient
{
    public static async Task<bool> RunAsync(string lockfilePath, CancellationToken ct)
    {
        // 1) Discover the server exactly like the CLI does: port from the filename, token from JSON.
        int port = int.Parse(Path.GetFileNameWithoutExtension(lockfilePath));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(lockfilePath, ct));
        string token = doc.RootElement.GetProperty("authToken").GetString()!;
        var uri = new Uri($"ws://127.0.0.1:{port}/");

        var asserts = new Asserts();
        Log.Info("=== self-test starting ===");

        // 2) Negative auth: a wrong token must be rejected at the upgrade (no socket).
        bool rejected = false;
        try
        {
            using var bad = NewSocket(token + "-wrong");
            await bad.ConnectAsync(uri, ct);
        }
        catch (WebSocketException)
        {
            rejected = true;
        }
        asserts.Check("auth: wrong token is rejected (401, no socket)", rejected);

        // 3) Connect for real.
        using var ws = NewSocket(token);
        await ws.ConnectAsync(uri, ct);
        asserts.Check("auth: correct token connects", ws.State == WebSocketState.Open);

        var rpc = new Rpc(ws);

        // 4) initialize handshake.
        var init = await rpc.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "spike-test-client", ["version"] = "0.1.0" },
        }, ct);
        asserts.Check("initialize: returns serverInfo.name",
            init?["serverInfo"]?["name"]?.GetValue<string>() is { Length: > 0 });
        asserts.Check("initialize: returns protocolVersion",
            init?["protocolVersion"]?.GetValue<string>() is { Length: > 0 });

        await rpc.NotifyAsync("notifications/initialized", new JsonObject(), ct);

        // 5) tools/list advertises the core 4.
        var list = await rpc.RequestAsync("tools/list", new JsonObject(), ct);
        var names = (list?["tools"] as JsonArray)?.Select(t => t?["name"]?.GetValue<string>()).ToHashSet() ?? new();
        foreach (var expected in new[] { "openFile", "openDiff", "getCurrentSelection", "getDiagnostics" })
            asserts.Check($"tools/list: advertises {expected}", names.Contains(expected));

        // 6) getCurrentSelection (stub) returns a JSON-wrapped object.
        var sel = await rpc.CallToolAsync("getCurrentSelection", new JsonObject(), ct);
        asserts.Check("getCurrentSelection: success=true in wrapped JSON",
            TryParse(sel.text)?["success"]?.GetValue<bool>() == true);

        // 7) getDiagnostics returns the [{uri,diagnostics:[]}] envelope.
        var diag = await rpc.CallToolAsync("getDiagnostics",
            new JsonObject { ["uri"] = "file:///tmp/Example.cs" }, ct);
        asserts.Check("getDiagnostics: returns the envelope array",
            TryParse(diag.text) is JsonArray);

        // 8) openFile (stub).
        var open = await rpc.CallToolAsync("openFile",
            new JsonObject { ["filePath"] = "/tmp/Example.cs" }, ct);
        asserts.Check("openFile: opened=true", TryParse(open.text)?["opened"]?.GetValue<bool>() == true);

        // 9) openDiff: the deferred one. Server must block, then resolve. In --auto-accept-diffs
        //    mode it returns the verbatim string "DIFF_ACCEPTED".
        var diff = await rpc.CallToolAsync("openDiff", new JsonObject
        {
            ["old_file_path"] = "/tmp/Example.cs",
            ["new_file_path"] = "/tmp/Example.cs",
            ["new_file_contents"] = "// proposed by the spike test\n",
            ["tab_name"] = "spike-selftest-diff",
        }, ct);
        asserts.Check("openDiff: returns verbatim DIFF_ACCEPTED", diff.text == "DIFF_ACCEPTED");
        asserts.Check("openDiff: not flagged isError", !diff.isError);

        // 10) unknown tool surfaces an MCP tool error (isError), not a transport crash.
        var bad2 = await rpc.CallToolAsync("noSuchTool", new JsonObject(), ct);
        asserts.Check("unknown tool: isError=true", bad2.isError);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        Log.Info($"=== self-test {(asserts.AllPassed ? "PASSED" : "FAILED")}: " +
                 $"{asserts.Passed}/{asserts.Total} checks ===");
        return asserts.AllPassed;
    }

    private static ClientWebSocket NewSocket(string token)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
        return ws;
    }

    private static JsonNode? TryParse(string s)
    {
        try { return JsonNode.Parse(s); } catch { return null; }
    }

    /// <summary>Minimal JSON-RPC client over the socket: id-matched requests, skips notifications.</summary>
    private sealed class Rpc(ClientWebSocket ws)
    {
        private int _id;

        public async Task<JsonNode?> RequestAsync(string method, JsonNode @params, CancellationToken ct)
        {
            int id = ++_id;
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            }, ct);

            // Read until we see the response for this id (skip any interleaved notifications).
            while (true)
            {
                var msg = await ReceiveAsync(ct);
                if (msg?["id"]?.GetValue<int>() == id)
                {
                    if (msg["error"] is JsonObject err)
                        throw new InvalidOperationException($"RPC error: {err["message"]}");
                    return msg["result"];
                }
            }
        }

        public Task NotifyAsync(string method, JsonNode @params, CancellationToken ct)
            => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }, ct);

        /// <summary>Call a tool and unwrap the MCP CallToolResult into (text, isError).</summary>
        public async Task<(string text, bool isError)> CallToolAsync(string name, JsonNode args, CancellationToken ct)
        {
            var result = await RequestAsync("tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = args }, ct);
            var text = result?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            var isError = result?["isError"]?.GetValue<bool>() ?? false;
            return (text, isError);
        }

        private async Task SendAsync(JsonNode node, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }

        private async Task<JsonNode?> ReceiveAsync(CancellationToken ct)
        {
            var buf = new byte[8192];
            var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
    }

    private sealed class Asserts
    {
        public int Total { get; private set; }
        public int Passed { get; private set; }
        public bool AllPassed => Total == Passed;

        public void Check(string name, bool ok)
        {
            Total++;
            if (ok) { Passed++; Log.Info($"  PASS  {name}"); }
            else Log.Error($"  FAIL  {name}");
        }
    }
}
