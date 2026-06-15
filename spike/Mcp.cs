using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// Speaks MCP over JSON-RPC 2.0. Handles one inbound message at a time and returns the JSON to
/// send back (or null for notifications / fire-and-forget). The transport (WS) owns reading/writing
/// bytes; this class owns the protocol semantics. See build-plan.md §3 (MCP wire protocol).
/// </summary>
internal sealed class McpServer
{
    private const string ServerName = "claude-code-vs-spike";
    private const string ServerVersion = "0.1.0";
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly ToolRegistry _tools;
    private static readonly JsonDocument EmptyArgs = JsonDocument.Parse("{}");

    public McpServer(ToolRegistry tools) => _tools = tools;

    /// <summary>Process one inbound frame. Returns response JSON to send, or null if no reply is due.</summary>
    public async Task<string?> HandleAsync(string json, CancellationToken ct)
    {
        // Keep the document alive for the whole method: openDiff awaits a user decision before
        // returning, and tool args are backed by this document.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return null; // not a request/notification we understand (e.g. a stray response)

        var method = methodEl.GetString()!;
        JsonNode? id = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
        var hasParams = root.TryGetProperty("params", out var paramsEl);

        switch (method)
        {
            case "initialize":
                return Response(id, BuildInitializeResult(hasParams ? paramsEl : default));

            case "tools/list":
                return Response(id, BuildToolsList());

            case "tools/call":
                return Response(id, await BuildToolCallResultAsync(hasParams ? paramsEl : default, ct));

            case "ping":
                return Response(id, new JsonObject());

            default:
                // Notifications (no id) we don't handle - just log and stay quiet.
                if (id is null)
                {
                    Log.Event($"ignoring notification: {method}");
                    return null;
                }
                return Error(id, -32601, $"method not found: {method}");
        }
    }

    private static JsonObject BuildInitializeResult(JsonElement @params)
    {
        // Echo the client's protocolVersion when offered, so we never mismatch.
        string protocolVersion = DefaultProtocolVersion;
        if (@params.ValueKind == JsonValueKind.Object &&
            @params.TryGetProperty("protocolVersion", out var pv) &&
            pv.ValueKind == JsonValueKind.String)
        {
            protocolVersion = pv.GetString()!;
        }

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
    }

    private JsonObject BuildToolsList()
    {
        var arr = new JsonArray();
        foreach (var tool in _tools.All)
        {
            arr.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = $"{tool.Name} (spike)",
                ["inputSchema"] = tool.Schema,
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private async Task<JsonObject> BuildToolCallResultAsync(JsonElement @params, CancellationToken ct)
    {
        string? name = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("name", out var n)
            ? n.GetString()
            : null;

        if (name is null)
            return ToolResult("missing tool name", isError: true);

        if (!_tools.TryGet(name, out var tool))
            return ToolResult($"unknown tool: {name}", isError: true);

        JsonElement args = @params.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : EmptyArgs.RootElement;

        try
        {
            object result = await tool.InvokeAsync(args, ct);
            // Wire format: plain string -> verbatim; anything else -> JSON-stringified.
            string text = result is string s ? s : JsonSerializer.Serialize(result);
            return ToolResult(text, isError: false);
        }
        catch (Exception e)
        {
            Log.Error($"tool '{name}' threw: {e.Message}");
            return ToolResult($"{name} failed: {e.Message}", isError: true);
        }
    }

    // ---- JSON-RPC / MCP envelope builders ----

    private static JsonObject ToolResult(string text, bool isError)
    {
        var obj = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError) obj["isError"] = true;
        return obj;
    }

    private static string Response(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
