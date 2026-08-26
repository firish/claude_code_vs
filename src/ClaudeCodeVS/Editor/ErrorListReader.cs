using System;
using System.Collections.Generic;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// Reads the Visual Studio Error List and groups entries by file in LSP shape. The Error List is a
/// unified sink: Roslyn pushes live C#/.NET diagnostics into it, and the C++ toolchain pushes its
/// errors/warnings too - so this single path serves both languages (build-plan §5; C++ is the #15942
/// audience). Ranges are point ranges (the Error List exposes a single line/column).
/// </summary>
/// <summary>One Error List row, flattened. <see cref="File"/> is empty for entries with no document
/// (an MSBuild/project-level failure); <see cref="Severity"/> is the LSP scale (1=Error, 2=Warning, 3=Info).</summary>
internal sealed class ErrorEntry
{
    public string File = "";
    public int Line;      // 0-based, as the Error List reports it
    public int Column;    // 0-based
    public string Message = "";
    public int Severity = 1;
    public string? Project;
}

internal static class ErrorListReader
{
    /// <summary>Map of absolute file path -> array of LSP diagnostic objects. Call on the UI thread.</summary>
    public static Dictionary<string, JArray> Read()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in ReadEntries())
        {
            // getDiagnostics is file-keyed, so project-level entries with no document are dropped here.
            // vs_build reads ReadEntries() directly precisely so it can still report them.
            if (string.IsNullOrEmpty(e.File)) continue;

            var diag = new JObject
            {
                ["message"] = e.Message,
                ["severity"] = e.Severity,
                ["source"] = "Visual Studio",
                ["range"] = new JObject
                {
                    ["start"] = new JObject { ["line"] = e.Line, ["character"] = e.Column },
                    ["end"] = new JObject { ["line"] = e.Line, ["character"] = e.Column },
                },
            };

            if (!byFile.TryGetValue(e.File, out var list))
                byFile[e.File] = list = new JArray();
            list.Add(diag);
        }

        return byFile;
    }

    /// <summary>
    /// Every Error List row as a flat, structured entry - including the ones with no document. This is the
    /// language-neutral way to get build diagnostics: the categories come back as an enum, so unlike parsing
    /// MSBuild's output text (where "error"/"warning" are localized) it reads the same in any VS UI language.
    /// Call on the UI thread.
    /// </summary>
    public static List<ErrorEntry> ReadEntries()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var entries = new List<ErrorEntry>();

        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) is not IVsTaskList taskList)
            return entries;

        if (taskList.EnumTaskItems(out IVsEnumTaskItems en) != VSConstants.S_OK || en is null)
            return entries;

        var items = new IVsTaskItem[1];
        var fetched = new uint[1];

        while (en.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
        {
            var item = items[0];
            if (item is null) continue;

            string? doc = null;
            try { item.Document(out doc); } catch { /* some items have no document */ }

            int line = 0, col = 0;
            try { item.Line(out line); } catch { }
            try { item.Column(out col); } catch { }
            if (line < 0) line = 0;
            if (col < 0) col = 0;

            string? text = null;
            try { item.get_Text(out text); } catch { }

            if (IsTransientDiffArtifact(doc)) continue;

            var entry = new ErrorEntry
            {
                File = doc ?? "",
                Line = line,
                Column = col,
                Message = text ?? "",
            };

            if (item is IVsErrorItem err)
            {
                if (err.GetCategory(out uint cat) == VSConstants.S_OK)
                    entry.Severity = CategoryToLspSeverity(cat);
                entry.Project = ProjectNameOf(err);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static readonly string TempRoot =
        (System.IO.Path.GetTempPath() ?? "").TrimEnd('\\', '/');

    /// <summary>
    /// True for Error List rows that belong to a diff's throwaway staging file rather than to the user's
    /// code. Opening a staged file in the diff viewer gets it analyzed as a *miscellaneous* file, and those
    /// rows OUTLIVE the file - they survive its deletion and subsequent builds, so they pile up one set per
    /// edit and inflate every error/warning count that reads the Error List. Filtering here fixes both
    /// consumers at once (<see cref="Read"/> for getDiagnostics and <see cref="ReadEntries"/> for vs_build).
    ///
    /// Two rules: our own staging files by name, and anything else under the temp dir that no longer exists
    /// (a row for a deleted temp file is stale by definition). The existence check is deliberately scoped to
    /// temp paths so a big Error List doesn't pay a stat per row.
    /// </summary>
    private static bool IsTransientDiffArtifact(string? file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        try
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.StartsWith("claudediff_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("claudeperm_", StringComparison.OrdinalIgnoreCase))
                return true;

            if (TempRoot.Length > 0 &&
                file!.StartsWith(TempRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return !System.IO.File.Exists(file);
        }
        catch { /* unparseable path -> keep the row */ }
        return false;
    }

    /// <summary>The owning project's display name for an error row, via its hierarchy. Best-effort.</summary>
    private static string? ProjectNameOf(IVsErrorItem err)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (err.GetHierarchy(out IVsHierarchy hier) != VSConstants.S_OK || hier is null) return null;
            if (hier.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Name, out object name) != VSConstants.S_OK)
                return null;
            return name as string;
        }
        catch { return null; }
    }

    // __VSERRORCATEGORY: EC_ERROR=0, EC_WARNING=1, EC_MESSAGE=2  ->  LSP: 1=Error,2=Warning,3=Information
    private static int CategoryToLspSeverity(uint category) => category switch
    {
        0 => 1,
        1 => 2,
        _ => 3,
    };
}
