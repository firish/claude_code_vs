using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// openFile - open a document in the editor and optionally select/reveal a line range (build-plan §5).
/// Lines from the protocol are treated as 0-based (consistent with selection/diagnostics). startText/
/// endText selection and the preview tab are Phase 2; we honor filePath, startLine/endLine, makeFrontmost.
/// </summary>
internal sealed class OpenFileTool : IIdeTool
{
    public string Name => "openFile";
    public string Description => "Open a file in the Visual Studio editor and optionally select/reveal a line range.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["filePath"] = new JObject { ["type"] = "string" },
            ["preview"] = new JObject { ["type"] = "boolean", ["default"] = false },
            ["startLine"] = new JObject { ["type"] = "integer" },
            ["endLine"] = new JObject { ["type"] = "integer" },
            ["startText"] = new JObject { ["type"] = "string" },
            ["endText"] = new JObject { ["type"] = "string" },
            ["makeFrontmost"] = new JObject { ["type"] = "boolean", ["default"] = true },
        },
        ["required"] = new JArray("filePath"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? path = (string?)args["filePath"];
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("filePath is required");

        int? startLine = (int?)args["startLine"];
        int? endLine = (int?)args["endLine"];
        bool makeFrontmost = args["makeFrontmost"]?.Type != JTokenType.Boolean || (bool)args["makeFrontmost"]!;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Shared with the panel's Edits list, so a jump-to-change behaves exactly like the CLI's openFile.
        ClaudeCodeVs.Editor.Navigator.OpenAt(path!, startLine, endLine, makeFrontmost);

        Log.Info($"openFile: {path} (line {startLine?.ToString() ?? "-"})");
        return new JObject { ["opened"] = true, ["filePath"] = path };
    }
}
