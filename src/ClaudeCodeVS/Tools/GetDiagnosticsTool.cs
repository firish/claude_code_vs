using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// getDiagnostics - language/build diagnostics from the VS Error List, grouped per file. Always
/// returns the envelope [{uri, diagnostics:[...]}] (empty array if none), per build-plan §4. When a
/// uri is supplied we filter to that file and still return its (possibly empty) envelope.
/// </summary>
internal sealed class GetDiagnosticsTool : IIdeTool
{
    public string Name => "getDiagnostics";
    public string Description => "Get compiler/build diagnostics (errors and warnings) from Visual Studio's Error List, optionally filtered to a single file URI. Returns [{uri, diagnostics:[...]}].";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["uri"] = new JObject { ["type"] = "string" } },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? uriFilter = (string?)args["uri"];
        string? pathFilter = TryUriToPath(uriFilter);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var byFile = ErrorListReader.Read();

        var result = new JArray();
        bool matchedFilter = false;

        foreach (var kv in byFile)
        {
            if (pathFilter is not null && !PathEquals(kv.Key, pathFilter))
                continue;
            matchedFilter = true;
            result.Add(new JObject
            {
                ["uri"] = PathToUri(kv.Key),
                ["diagnostics"] = kv.Value,
            });
        }

        // A specific file with no diagnostics still gets an (empty) envelope.
        if (pathFilter is not null && !matchedFilter)
        {
            result.Add(new JObject
            {
                ["uri"] = uriFilter ?? PathToUri(pathFilter),
                ["diagnostics"] = new JArray(),
            });
        }

        Log.Info($"getDiagnostics: uri={uriFilter ?? "(all)"} -> {result.Count} file(s)");
        return result;
    }

    private static string? TryUriToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        try { return uri!.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : uri; }
        catch { return uri; }
    }

    private static string PathToUri(string path)
    {
        try { return new Uri(path).AbsoluteUri; } catch { return path; }
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            a.Replace('/', '\\').TrimEnd('\\'),
            b.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
