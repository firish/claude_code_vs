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
internal static class ErrorListReader
{
    /// <summary>Map of absolute file path -> array of LSP diagnostic objects. Call on the UI thread.</summary>
    public static Dictionary<string, JArray> Read()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);

        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) is not IVsTaskList taskList)
            return byFile;

        if (taskList.EnumTaskItems(out IVsEnumTaskItems en) != VSConstants.S_OK || en is null)
            return byFile;

        var items = new IVsTaskItem[1];
        var fetched = new uint[1];

        while (en.Next(1, items, fetched) == VSConstants.S_OK && fetched[0] == 1)
        {
            var item = items[0];
            if (item is null) continue;

            string? doc = null;
            try { item.Document(out doc); } catch { /* some items have no document */ }
            if (string.IsNullOrEmpty(doc)) continue;

            int line = 0, col = 0;
            try { item.Line(out line); } catch { }
            try { item.Column(out col); } catch { }
            if (line < 0) line = 0;
            if (col < 0) col = 0;

            string? text = null;
            try { item.get_Text(out text); } catch { }

            int severity = 1; // LSP Error
            if (item is IVsErrorItem err && err.GetCategory(out uint cat) == VSConstants.S_OK)
                severity = CategoryToLspSeverity(cat);

            var diag = new JObject
            {
                ["message"] = text ?? "",
                ["severity"] = severity,
                ["source"] = "Visual Studio",
                ["range"] = new JObject
                {
                    ["start"] = new JObject { ["line"] = line, ["character"] = col },
                    ["end"] = new JObject { ["line"] = line, ["character"] = col },
                },
            };

            if (!byFile.TryGetValue(doc!, out var list))
                byFile[doc!] = list = new JArray();
            list.Add(diag);
        }

        return byFile;
    }

    // __VSERRORCATEGORY: EC_ERROR=0, EC_WARNING=1, EC_MESSAGE=2  ->  LSP: 1=Error,2=Warning,3=Information
    private static int CategoryToLspSeverity(uint category) => category switch
    {
        0 => 1,
        1 => 2,
        _ => 3,
    };
}
