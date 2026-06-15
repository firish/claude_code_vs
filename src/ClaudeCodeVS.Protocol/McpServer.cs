using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// Speaks MCP over JSON-RPC 2.0. Handles one inbound message at a time and returns the JSON to
/// send back (or null for notifications / fire-and-forget). The transport (WS) owns reading/writing
/// bytes; this class owns the protocol semantics. See build-plan.md §3 (MCP wire protocol).
/// Uses Newtonsoft.Json (VS ships it with binding redirects for in-proc extensions).
/// </summary>
public sealed class McpServer
{
    private const string ServerName = "claude-code-vs";
    private const string ServerVersion = "0.1.0";
    // Echoed back to the client when it offers one; CLI 2.1.x sends "2025-11-25".
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly ToolRegistry _tools;

    public McpServer(ToolRegistry tools) => _tools = tools;

    /// <summary>Process one inbound frame. Returns response JSON to send, or null if no reply is due.</summary>
    public async Task<string?> HandleAsync(string json, CancellationToken ct)
    {
        JObject root;
        try { root = JObject.Parse(json); }
        catch { return null; } // not parseable as an object - ignore

        var method = (string?)root["method"];
        if (method is null)
            return null; // not a request/notification we understand (e.g. a stray response)

        JToken? id = root["id"];
        JToken? @params = root["params"];

        switch (method)
        {
            case "initialize":
                return Response(id, BuildInitializeResult(@params as JObject));

            case "tools/list":
                return Response(id, BuildToolsList());

            case "tools/call":
                return Response(id, await BuildToolCallResultAsync(@params as JObject, ct));

            case "ping":
                return Response(id, new JObject());

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

    private static JObject BuildInitializeResult(JObject? @params)
    {
        // Echo the client's protocolVersion when offered, so we never mismatch.
        string protocolVersion = (string?)@params?["protocolVersion"] ?? DefaultProtocolVersion;

        return new JObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JObject { ["tools"] = new JObject() },
            ["serverInfo"] = new JObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
    }

    private JObject BuildToolsList()
    {
        var arr = new JArray();
        foreach (var tool in _tools.All)
        {
            arr.Add(new JObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema,
            });
        }
        return new JObject { ["tools"] = arr };
    }

    private async Task<JObject> BuildToolCallResultAsync(JObject? @params, CancellationToken ct)
    {
        string? name = (string?)@params?["name"];
        if (name is null)
            return ToolResult("missing tool name", isError: true);

        if (!_tools.TryGet(name, out var tool))
            return ToolResult($"unknown tool: {name}", isError: true);

        JToken args = @params?["arguments"] as JObject ?? new JObject();

        try
        {
            object result = await tool.InvokeAsync(args, ct);
            // Wire format: plain string -> verbatim; JToken/anything else -> serialized compact.
            string text = result switch
            {
                string s => s,
                JToken t => t.ToString(Formatting.None),
                _ => JsonConvert.SerializeObject(result),
            };
            return ToolResult(text, isError: false);
        }
        catch (Exception e)
        {
            Log.Error($"tool '{name}' threw: {e.Message}");
            return ToolResult($"{name} failed: {e.Message}", isError: true);
        }
    }

    // ---- JSON-RPC / MCP envelope builders ----

    private static JObject ToolResult(string text, bool isError)
    {
        var obj = new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError) obj["isError"] = true;
        return obj;
    }

    private static string Response(JToken? id, JToken result)
        => new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = result }
            .ToString(Formatting.None);

    private static string Error(JToken? id, int code, string message)
        => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        }.ToString(Formatting.None);
}
