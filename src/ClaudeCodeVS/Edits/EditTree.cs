using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeCodeVs.Edits;

/// <summary>
/// The EDITS tab's folder tree. Each changed file is placed by its path relative to the most meaningful
/// base: the workspace folder when the file is inside it; otherwise the root of the git repository that
/// holds the file (shown under the repository's folder name); otherwise its full directory. Chains of
/// folders that hold nothing but one sub-folder are merged into one node ("src/ClaudeCodeVS/Ui"), the way
/// VS Code's compact folders do, so deep trees don't cost a row per level. Pure - no WPF.
/// </summary>
internal static class EditTree
{
    internal sealed class Folder
    {
        /// <summary>Display path of this node - one segment, or several joined with '/' after compaction.</summary>
        public string Name = "";
        /// <summary>Absolute directory (collapse-state key and tooltip).</summary>
        public string FullPath = "";
        public List<Folder> Folders = new();
        /// <summary>One entry per file: that file's rows, in the order they were recorded.</summary>
        public List<List<EditEntry>> Files = new();

        public IEnumerable<EditEntry> AllRows() =>
            Files.SelectMany(f => f).Concat(Folders.SelectMany(f => f.AllRows()));
    }

    public static Folder Build(IReadOnlyList<EditEntry> edits, string? workspace)
    {
        var root = new Folder();
        var baseDir = Normalize(workspace);
        foreach (var file in edits.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var rows = file.ToList();
            var (anchor, segments) = Place(Normalize(file.Key), baseDir);
            var node = root;
            var path = anchor;
            foreach (var segment in segments)
            {
                path = path.Length == 0 ? segment : Path.Combine(path, segment);
                var next = node.Folders.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = new Folder { Name = segment, FullPath = path };
                    node.Folders.Add(next);
                }
                node = next;
            }
            node.Files.Add(rows);
        }
        Sort(root);
        foreach (var child in root.Folders) Compact(child);
        return root;
    }

    /// <summary>
    /// Where a file hangs: the anchor directory its folder segments are relative to, and those segments
    /// (excluding the file name). Workspace files have no extra top node; repository files hang under the
    /// repository folder's name; anything else under its own full directory as a single node.
    /// </summary>
    private static (string Anchor, string[] Segments) Place(string file, string baseDir)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        if (baseDir.Length > 0 && IsUnder(dir, baseDir, orEqual: true))
            return (baseDir, Split(dir.Substring(baseDir.Length)));

        var repo = RepoRoot(dir);
        if (repo != null)
        {
            var parent = Path.GetDirectoryName(repo) ?? "";
            return (parent, new[] { Path.GetFileName(repo) }.Concat(Split(dir.Substring(repo.Length))).ToArray());
        }
        return ("", new[] { dir });
    }

    private static readonly ConcurrentDictionary<string, string?> RepoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The nearest enclosing directory that has a .git entry (folder or worktree file), or null.</summary>
    private static string? RepoRoot(string dir) => RepoCache.GetOrAdd(dir, d =>
    {
        try
        {
            for (var cur = d; !string.IsNullOrEmpty(cur); cur = Path.GetDirectoryName(cur))
            {
                var git = Path.Combine(cur, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return cur.TrimEnd('\\');
            }
        }
        catch { /* unreadable path */ }
        return null;
    });

    private static void Sort(Folder node)
    {
        node.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        node.Files.Sort((a, b) => string.Compare(a[0].FileName, b[0].FileName, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Folders) Sort(child);
    }

    /// <summary>Merge a folder with its only sub-folder while it holds no files of its own.</summary>
    private static void Compact(Folder node)
    {
        while (node.Files.Count == 0 && node.Folders.Count == 1)
        {
            var only = node.Folders[0];
            node.Name = node.Name + "/" + only.Name;
            node.FullPath = only.FullPath;
            node.Folders = only.Folders;
            node.Files = only.Files;
        }
        foreach (var child in node.Folders) Compact(child);
    }

    /// <summary>
    /// Fit a '/'-joined path into <paramref name="max"/> characters keeping its MOST significant part - the
    /// deepest segments: "src/ClaudeCodeVS/Ui/Panels" -> "…/Ui/Panels". The last segment always stays whole.
    /// </summary>
    public static string Shorten(string path, int max)
    {
        if (path.Length <= max) return path;
        // Compacted nodes join with '/', an outside-the-repo node is a Windows path with '\' - keep its own.
        char sep = path.IndexOf('/') >= 0 ? '/' : '\\';
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return path;
        var kept = parts[parts.Length - 1];
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var candidate = parts[i] + sep + kept;
            if (candidate.Length + 2 > max) break;
            kept = candidate;
        }
        return "…" + sep + kept;
    }

    private static string[] Split(string relative) =>
        relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsUnder(string path, string root, bool orEqual) =>
        (orEqual && string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFullPath(path!.Replace('/', '\\')).TrimEnd('\\'); }
        catch { return path!.Replace('/', '\\').TrimEnd('\\'); }
    }
}
