using System;
using System.Collections.Generic;

namespace ClaudeCodeVs.Edits;

/// <summary>One navigable change within a file: a 0-based, inclusive line range in the NEW content.</summary>
internal sealed class EditHunk
{
    public int StartLine;   // 0-based, in the new file - what we navigate to
    public int EndLine;     // 0-based, inclusive
    public int Added;       // lines present in the new content
    public int Removed;     // lines the change replaced

    /// <summary>1-based label, e.g. "22" or "47-51".</summary>
    public string LineLabel => StartLine == EndLine
        ? (StartLine + 1).ToString()
        : $"{StartLine + 1}-{EndLine + 1}";
}

/// <summary>What one edit did to one file.</summary>
internal sealed class EditSummary
{
    public List<EditHunk> Hunks = new();

    /// <summary>Too many separate hunks to list (a reformat, a generated rewrite): show one summary row.</summary>
    public bool Rewritten;

    public int TotalAdded;
    public int TotalRemoved;
    public int NewLineCount;

    public bool HasChanges => Rewritten || Hunks.Count > 0;
}

/// <summary>
/// Line diff between a file's current content and the content an edit proposes, reduced to the hunks the
/// Edits panel offers as jump targets. Pure and VS-free on purpose: this is the part worth testing
/// headlessly, and the panel and (later) the Output pane are both thin adapters over it.
///
/// The CLI prints one reference per edit; a hunk list is finer than that, which is the point. A whole-file
/// rewrite would invert the benefit and flood the section with hundreds of rows, so past
/// <see cref="MaxHunks"/> the result collapses to a single "rewritten" row.
/// </summary>
internal static class EditHunks
{
    /// <summary>Above this many separate hunks, collapse to one summary row.</summary>
    public const int MaxHunks = 20;

    /// <summary>Unchanged lines between two changed regions that still read as one hunk.</summary>
    private const int MergeGap = 2;

    /// <summary>DP table cells we're willing to allocate before falling back to one coarse hunk.</summary>
    private const long MaxDiffCells = 250_000;

    public static EditSummary Compute(string? oldText, string? newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var result = new EditSummary { NewLineCount = newLines.Length };

        // Trim the identical head and tail: on a typical single-hunk edit this reduces the diff to a
        // handful of lines and the expensive path below never runs.
        int head = 0;
        int maxHead = Math.Min(oldLines.Length, newLines.Length);
        while (head < maxHead && string.Equals(oldLines[head], newLines[head], StringComparison.Ordinal))
            head++;

        int tail = 0;
        int maxTail = Math.Min(oldLines.Length, newLines.Length) - head;
        while (tail < maxTail &&
               string.Equals(oldLines[oldLines.Length - 1 - tail], newLines[newLines.Length - 1 - tail], StringComparison.Ordinal))
            tail++;

        int oldMid = oldLines.Length - tail - head;
        int newMid = newLines.Length - tail - head;
        if (oldMid <= 0 && newMid <= 0) return result; // identical

        // A pure insert or delete, or a region too big to align cheaply, is one hunk spanning the change.
        if (oldMid <= 0 || newMid <= 0 || (long)oldMid * newMid > MaxDiffCells)
        {
            AddHunk(result, head, Math.Max(head, head + newMid - 1), Math.Max(0, newMid), Math.Max(0, oldMid));
            Finish(result);
            return result;
        }

        AlignAndCollect(result, oldLines, newLines, head, oldMid, newMid);
        Finish(result);
        return result;
    }

    /// <summary>LCS-align the changed region and turn the gaps between matches into hunks.</summary>
    private static void AlignAndCollect(EditSummary result, string[] oldLines, string[] newLines, int head, int oldMid, int newMid)
    {
        // lcs[i, j] = length of the longest common subsequence of old[i..] and new[j..].
        var lcs = new int[oldMid + 1, newMid + 1];
        for (int i = oldMid - 1; i >= 0; i--)
            for (int j = newMid - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(oldLines[head + i], newLines[head + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int oi = 0, ni = 0;
        int runStart = -1, runAdded = 0, runRemoved = 0, runEnd = -1;

        void CloseRun()
        {
            if (runStart < 0) return;
            // runEnd stays -1 for a pure deletion: it has no line of its own, so the row points at the
            // single spot where the text was removed.
            AddHunk(result, runStart, runEnd < 0 ? runStart : Math.Max(runStart, runEnd), runAdded, runRemoved);
            runStart = -1; runAdded = 0; runRemoved = 0; runEnd = -1;
        }

        while (oi < oldMid || ni < newMid)
        {
            bool match = oi < oldMid && ni < newMid &&
                         string.Equals(oldLines[head + oi], newLines[head + ni], StringComparison.Ordinal);
            if (match)
            {
                // Only close the current run once enough unchanged lines have gone by, so a one-line
                // island between two edits doesn't split into two rows pointing at the same place.
                if (runStart >= 0 && ni - runEnd - 1 >= MergeGap) CloseRun();
                oi++; ni++;
                continue;
            }

            if (runStart < 0) { runStart = head + ni; runEnd = -1; }

            bool takeNew = ni < newMid && (oi >= oldMid || lcs[oi, ni + 1] >= lcs[oi + 1, ni]);
            if (takeNew)
            {
                runEnd = head + ni;
                runAdded++;
                ni++;
            }
            else
            {
                // A deletion occupies no line in the NEW file, so it must not widen the range - doing so
                // made every replacement report one line too many (a one-line change read as "3-4").
                runRemoved++;
                oi++;
            }
        }
        CloseRun();
    }

    private static void AddHunk(EditSummary r, int start, int end, int added, int removed)
    {
        if (added == 0 && removed == 0) return;
        r.Hunks.Add(new EditHunk
        {
            StartLine = Math.Max(0, start),
            EndLine = Math.Max(0, Math.Max(start, end)),
            Added = added,
            Removed = removed,
        });
    }

    /// <summary>Below this many lines a file is small enough that "most of it changed" means nothing.</summary>
    private const int RewriteMinLines = 25;

    /// <summary>Share of the file that has to be new before the hunk rows stop being the useful view.</summary>
    private const double RewriteFraction = 0.5;

    private static void Finish(EditSummary r)
    {
        foreach (var h in r.Hunks) { r.TotalAdded += h.Added; r.TotalRemoved += h.Removed; }

        // Two flood shapes. Many scattered hunks is the obvious one. The other is a change on every other
        // line, which MERGES into a single hunk spanning the file - one row labelled "Foo.cs:1-600" that
        // is neither a useful jump target nor honest about what happened. Judge it by how much of the file
        // is new rather than by the span, so two small edits at opposite ends of a file don't trip it.
        bool tooManyHunks = r.Hunks.Count > MaxHunks;
        bool mostlyNew = r.Hunks.Count > 0
                      && r.NewLineCount >= RewriteMinLines
                      && r.TotalAdded >= r.NewLineCount * RewriteFraction;

        if (tooManyHunks || mostlyNew)
        {
            r.Rewritten = true;
            r.Hunks.Clear(); // the rows would be noise; the summary row carries the totals
        }
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // A trailing newline yields a final empty element that is not a real line.
        if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
        {
            var trimmed = new string[lines.Length - 1];
            Array.Copy(lines, trimmed, trimmed.Length);
            return trimmed;
        }
        return lines;
    }
}
