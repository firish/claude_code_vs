using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Diff;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// openDiff — the centerpiece. The tool call must NOT return until the user decides, so we park a
/// TaskCompletionSource (via <see cref="DiffDecisions"/>) and block on it; the UI completes it later
/// (CLAUDE.md convention #3). The decision UI is VS's native diff viewer plus an Accept/Reject
/// InfoBar, rendered by <see cref="DiffSession"/>. On accept the proposed contents are written back.
/// </summary>
internal sealed class OpenDiffTool : IIdeTool
{
    private readonly DiffDecisions _decisions;

    public OpenDiffTool(DiffDecisions decisions) => _decisions = decisions;

    public string Name => "openDiff";
    public string Description => "Show a diff between an on-disk file and proposed new contents in Visual Studio's diff viewer, and wait for the user to accept or reject.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["old_file_path"] = new JObject { ["type"] = "string" },
            ["new_file_path"] = new JObject { ["type"] = "string" },
            ["new_file_contents"] = new JObject { ["type"] = "string" },
            ["tab_name"] = new JObject { ["type"] = "string" },
        },
        ["required"] = new JArray("old_file_path", "new_file_path", "new_file_contents", "tab_name"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        var oldPath = (string?)args["old_file_path"] ?? "";
        var newPath = (string?)args["new_file_path"] ?? "";
        var contents = (string?)args["new_file_contents"] ?? "";
        var tabName = (string?)args["tab_name"] is { } tn && tn.Length > 0
            ? tn
            : Guid.NewGuid().ToString();

        // Gotcha (CLAUDE.md): new_file_contents is in-memory. Stage it to a temp file so the real
        // diff viewer (next step) has something on disk to compare against.
        var temp = Path.Combine(Path.GetTempPath(), $"claudediff_{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temp, contents); } catch (Exception e) { Log.Warn($"temp stage failed: {e.Message}"); }

        var decision = _decisions.AwaitDecisionAsync(tabName);
        Log.Info($"openDiff: tab='{tabName}' old='{oldPath}' new='{newPath}' ({contents.Length} chars)");

        // Open the diff + InfoBar on the UI thread. DiffSession completes the parked decision (and
        // cleans up the temp file) when the user clicks Accept/Reject; this call blocks on it below.
        // Intentional fire-and-forget: FileAndForget reports faults to the activity log, and we await
        // the decision (not this task). VSSDK007 can't see that, so suppress it here.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                DiffSession.Open(oldPath, newPath, contents, tabName, temp, _decisions);
            }
            catch (Exception e)
            {
                Log.Error($"openDiff UI failed: {e.Message}");
                try { File.Delete(temp); } catch { /* best effort */ }
                _decisions.Resolve(tabName, false);
            }
        }).FileAndForget("claudecodevs/openDiff");
#pragma warning restore VSSDK007

        bool ok = await decision; // BLOCKS until the user decides
        Log.Info($"openDiff: {(ok ? "ACCEPTED" : "REJECTED")} '{tabName}'");

        return ok ? "DIFF_ACCEPTED" : "DIFF_REJECTED";
    }
}
