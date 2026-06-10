using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Diff;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>close_tab — close the diff window for a given tab_name (part of the core diff flow).</summary>
internal sealed class CloseTabTool : IIdeTool
{
    public string Name => "close_tab";
    public string Description => "Close a diff tab in Visual Studio by its tab name.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["tab_name"] = new JObject { ["type"] = "string" } },
        ["required"] = new JArray("tab_name"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var tabName = (string?)args["tab_name"];
        if (!string.IsNullOrEmpty(tabName))
            await DiffRegistry.CloseTabAsync(tabName!);
        return "TAB_CLOSED";
    }
}

/// <summary>closeAllDiffTabs — close every open diff (the CLI calls this on connect to clear leftovers).</summary>
internal sealed class CloseAllDiffTabsTool : IIdeTool
{
    public string Name => "closeAllDiffTabs";
    public string Description => "Close all open diff tabs in Visual Studio.";
    public JToken Schema => Schemas.Empty();

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await DiffRegistry.CloseAllAsync();
        return "CLOSED_ALL_DIFF_TABS";
    }
}
