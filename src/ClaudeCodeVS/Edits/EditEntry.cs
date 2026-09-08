using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCodeVs.Edits;

/// <summary>
/// One row in the panel's "Edits this turn" list: a place in a file that Claude just changed, and enough
/// detail to render it. Deliberately a flat row per hunk rather than a tree grouped by file - a single
/// turn rarely produces enough entries for grouping to pay for itself.
/// </summary>
internal sealed class EditEntry
{
    public string FilePath = "";
    public string FileName = "";
    public int StartLine;          // 0-based, in the file as it now stands
    public int EndLine;            // 0-based, inclusive
    public int Added;
    public int Removed;
    public bool Rewritten;         // too many hunks to list: this row stands for the whole file
    public int TotalLines;         // for the rewritten label
    public DateTime When = DateTime.Now;

    /// <summary>1-based line label, e.g. "22" or "47-51". Empty for a rewritten-file row.</summary>
    public string LineLabel => Rewritten
        ? ""
        : (StartLine == EndLine ? (StartLine + 1).ToString() : $"{StartLine + 1}-{EndLine + 1}");

    /// <summary>"+3 -1", or empty when nothing useful to show.</summary>
    public string ChangeLabel
    {
        get
        {
            if (Added > 0 && Removed > 0) return $"+{Added} -{Removed}";
            if (Added > 0) return $"+{Added}";
            if (Removed > 0) return $"-{Removed}";
            return "";
        }
    }

    /// <summary>Turn one edit's computed hunks into rows. Empty when the edit changed nothing.</summary>
    public static List<EditEntry> From(string filePath, EditSummary summary)
    {
        var rows = new List<EditEntry>();
        if (!summary.HasChanges) return rows;

        string name;
        try { name = Path.GetFileName(filePath); } catch { name = filePath; }
        if (string.IsNullOrEmpty(name)) name = filePath;

        if (summary.Rewritten)
        {
            rows.Add(new EditEntry
            {
                FilePath = filePath,
                FileName = name,
                Rewritten = true,
                TotalLines = summary.NewLineCount,
                Added = summary.TotalAdded,
                Removed = summary.TotalRemoved,
                StartLine = 0,
                EndLine = 0,
            });
            return rows;
        }

        foreach (var h in summary.Hunks)
        {
            rows.Add(new EditEntry
            {
                FilePath = filePath,
                FileName = name,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                Added = h.Added,
                Removed = h.Removed,
            });
        }
        return rows;
    }
}
