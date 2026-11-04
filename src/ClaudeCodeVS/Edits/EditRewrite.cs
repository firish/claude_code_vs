using System;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Edits;

/// <summary>
/// Turns "the user edited Claude's proposal in the diff" into something the CLI can apply: a replacement
/// <c>tool_input</c> for the PreToolUse hook's <c>updatedInput</c> (edit-before-accept). The CLI stays the
/// sole writer, exactly as in the plain accept path; we only change WHAT it writes.
///
/// The hook contract says updatedInput replaces the entire input object, so each shape below is complete.
/// Text is LF-normalized throughout: the model's own strings are LF, and the CLI's Edit tool matches them
/// against an LF-normalized read of the file and restores the file's real line endings on write - the
/// same fact the permission hook's ApplyEdit relies on in the other direction.
///
/// Pure and static, so the region-finding below can be checked headlessly.
/// </summary>
internal static class EditRewrite
{
    /// <summary>True for the tools whose input this class knows how to rebuild.</summary>
    public static bool CanRewrite(string? toolName) => toolName is "Edit" or "Write" or "MultiEdit";

    /// <summary>
    /// The complete replacement input for <paramref name="toolName"/>, or null when the user's version
    /// is identical to the file as it already is (nothing to write - the caller should deny instead).
    /// <paramref name="before"/> is the file's current content, null for a file that does not exist yet.
    /// </summary>
    public static JObject? BuildUpdatedInput(string toolName, string filePath, string? before, string edited)
    {
        var a = before == null ? null : Lf(before);
        var b = Lf(edited);

        switch (toolName)
        {
            case "Write":
                return new JObject { ["file_path"] = filePath, ["content"] = b };

            case "Edit":
            case "MultiEdit":
            {
                string oldString, newString;
                if (string.IsNullOrEmpty(a))
                {
                    // A new (or empty) file: the CLI's Edit creates it when old_string is empty.
                    oldString = "";
                    newString = b;
                }
                else
                {
                    if (string.Equals(a, b, StringComparison.Ordinal)) return null;
                    (oldString, newString) = MinimalUniqueReplacement(a!, b);
                }

                if (toolName == "Edit")
                    return new JObject
                    {
                        ["file_path"] = filePath,
                        ["old_string"] = oldString,
                        ["new_string"] = newString,
                        ["replace_all"] = false,
                    };
                return new JObject
                {
                    ["file_path"] = filePath,
                    ["edits"] = new JArray(new JObject { ["old_string"] = oldString, ["new_string"] = newString }),
                };
            }

            default:
                return null; // not a tool whose input we know how to rewrite
        }
    }

    /// <summary>
    /// The smallest whole-line region of <paramref name="a"/> that, replaced, yields <paramref name="b"/> -
    /// widened line by line until it occurs exactly once in <paramref name="a"/>, which is what the CLI's
    /// Edit tool demands of old_string. Falls back to the whole file, which is always unique.
    /// </summary>
    internal static (string oldString, string newString) MinimalUniqueReplacement(string a, string b)
    {
        int prefix = 0;
        int max = Math.Min(a.Length, b.Length);
        while (prefix < max && a[prefix] == b[prefix]) prefix++;

        int suffix = 0;
        while (suffix < max - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        // Snap to line boundaries: back to the start of the line the change begins on, forward past the
        // end of the line it ends on (the same extension applies to both sides - the suffix is shared).
        int start = prefix == 0 ? 0 : a.LastIndexOf('\n', prefix - 1) + 1;
        int endA = a.Length - suffix;
        int lineEnd = a.IndexOf('\n', endA);
        int extend = (lineEnd < 0 ? a.Length : lineEnd + 1) - endA;
        endA += extend;
        int endB = b.Length - suffix + extend;

        for (int guard = 0; guard < 400; guard++)
        {
            string oldString = a.Substring(start, endA - start);
            if (oldString.Length > 0 && CountOccurrences(a, oldString) == 1)
                return (oldString, b.Substring(start, endB - start));

            if (start == 0 && endA == a.Length) break; // whole file already

            // Widen by one line on each side.
            if (start > 0) start = start <= 1 ? 0 : a.LastIndexOf('\n', start - 2) + 1;
            if (endA < a.Length)
            {
                int next = a.IndexOf('\n', endA);
                int grow = (next < 0 ? a.Length : next + 1) - endA;
                endA += grow;
                endB += grow;
            }
        }
        return (a, b);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            if (count > 1) return count;
            idx += needle.Length;
        }
        return count;
    }

    internal static string Lf(string s) => s.IndexOf('\r') < 0 ? s : s.Replace("\r\n", "\n").Replace('\r', '\n');
}
