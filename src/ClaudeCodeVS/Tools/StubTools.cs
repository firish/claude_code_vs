using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

// ---------------------------------------------------------------------------
// Schema helpers + the parity stubs. The core 4 (openFile, openDiff, selection, diagnostics) are
// real; the remaining tools are Phase-1 stubs that keep the CLI happy.
// ---------------------------------------------------------------------------

internal static class Schemas
{
    public static JToken Empty() => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public static JToken WithFilePath(bool required = true)
    {
        var o = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject { ["filePath"] = new JObject { ["type"] = "string" } },
        };
        if (required) o["required"] = new JArray("filePath");
        return o;
    }
}

/// <summary>Compact IIdeTool for the parity stubs — name, schema, and a function body.</summary>
internal sealed class LambdaTool : IIdeTool
{
    private readonly Func<JToken, CancellationToken, Task<object>> _fn;

    public LambdaTool(string name, string description, JToken schema, Func<JToken, CancellationToken, Task<object>> fn)
    {
        Name = name;
        Description = description;
        Schema = schema;
        _fn = fn;
    }

    public string Name { get; }
    public string Description { get; }
    public JToken Schema { get; }
    public Task<object> InvokeAsync(JToken arguments, CancellationToken ct) => _fn(arguments, ct);
}

/// <summary>
/// The remaining parity tools as Phase-1 stubs. Per build-plan §3, close_tab / closeAllDiffTabs are
/// part of the *core* diff flow (the CLI calls them right after a diff and on connect), so they return
/// success no-ops rather than errors. executeCode has no VS equivalent → honest MCP error.
/// </summary>
internal static class ParityTools
{
    public static IEnumerable<IIdeTool> All()
    {
        // getOpenEditors / getWorkspaceFolders / checkDocumentDirty / saveDocument (WorkspaceTools.cs)
        // and close_tab / closeAllDiffTabs (CloseTabTools.cs) are now real, registered in BridgeHost.

        // No VS equivalent (Jupyter kernel execution) -> honest MCP error.
        yield return new LambdaTool("executeCode",
            "Execute code in a Jupyter kernel. Not supported in Visual Studio.",
            new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["code"] = new JObject { ["type"] = "string" } },
                ["required"] = new JArray("code"),
            },
            (a, ct) => throw new NotSupportedException("executeCode is not supported in Visual Studio"));
    }
}
