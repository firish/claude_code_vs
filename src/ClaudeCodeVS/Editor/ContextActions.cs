using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// The editor context-menu ("Claude Code" flyout) action bodies. Every action follows one shape:
/// stage a self-describing instruction .txt (insert-not-submit - it lands in the CLI composer for
/// the user to send) and @-mention the code it talks about, so the model gets both the ask and the
/// ground truth. Instruction text goes to the MODEL and stays English by design (CLAUDE.md #6);
/// feed lines are diagnostics and stay English too. If no session is connected, the action launches
/// one - staged attachments deliver on connect (the official VS Code extension's behavior).
/// </summary>
internal static class ContextActions
{
    // ---- Add to Chat: mention + focus (Alt+K and the menu entry both land here) --------------------

    public static async Task AddToChatAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (SelectionService.Current.FilePath is null)
        {
            Log.Warn("Add to Chat: no active file.");
            return;
        }
        // Launches like every other action in the flyout (issue #36): before, a cold-start Add to Chat
        // was the one entry that did nothing at all - no session, no staged item, just a warning.
        bool launched = EnsureSessionLaunching("Add to Chat");
        await SelectionService.MentionCurrentAsync();
        FocusClaude(launched);
    }

    // ---- Add to Chat (Solution Explorer): the selected files and folders --------------------------

    /// <summary>
    /// Solution Explorer's "Add to Chat": @-mention every selected file and folder, whole-file (the
    /// editor flyout above covers line ranges in the open document). Multi-select included, folders
    /// included - a folder @-mention is first-class in the CLI, which walks the tree itself.
    /// Routed through the attachment tray rather than raw mentions, so each item gets a chip with
    /// click-to-re-mention (the CLI drops mentions sent mid-turn) and re-sends itself on connect.
    /// </summary>
    public static async Task AddSelectionToChatAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // hierarchy read is UI-thread

        var paths = (await VS.Solutions.GetActiveItemsAsync())
            .Where(i => i.Type is SolutionItemType.PhysicalFile or SolutionItemType.PhysicalFolder)
            .Select(i => i.FullPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        if (paths.Count == 0)
        {
            Log.Warn("Add to Chat: the selection has no files or folders.");
            return;
        }
        if (paths.Count > MaxItemsPerAdd)
        {
            // The tray only holds 20 chips anyway - mentioning more would push the earlier ones out.
            Log.Info($"Add to Chat: {paths.Count} items selected - mentioning the first {MaxItemsPerAdd}.");
            paths = paths.Take(MaxItemsPerAdd).ToList();
        }

        bool launched = EnsureSessionLaunching("Add to Chat");
        await Attachments.AttachmentService.StageFilesAsync(paths!);
        FocusClaude(launched);
    }

    private const int MaxItemsPerAdd = 20; // == AttachmentService.MaxItems (the tray's chip bound)

    // ---- Explain: selection (embedded) or whole file (mentioned) ----------------------------------

    public static async Task ExplainAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Explain: no active file.");
            return;
        }
        bool launched = EnsureSessionLaunching("Explain");
        var fileName = System.IO.Path.GetFileName(sel.FilePath);

        if (!sel.IsEmpty)
        {
            int a = sel.StartLine + 1, b = sel.EndLineInclusive + 1;
            var header = $"Explain this code from {fileName} (lines {a}-{b}):";
            await Attachments.AttachmentService.StageTextAsync(
                header + "\n\n" + sel.Text, $"explain-{fileName}-L{a}-{b}");
            FocusClaude(launched);
            return;
        }

        // No selection: whole-file explain (parity with the official extension's "Explain File").
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;
        await Attachments.AttachmentService.StageTextAsync(
            $"Explain the file {rel} (mentioned alongside this note): its purpose, structure, and how the pieces fit together.",
            $"explain-{fileName}");
        await SelectionService.MentionAsync(sel.FilePath, null, null, "Explain");
        FocusClaude(launched);
    }

    // ---- Fix Errors: selection/file + its Error List diagnostics ----------------------------------

    public static async Task FixErrorsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // Error List read is UI-thread
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Fix Errors: no active file.");
            return;
        }
        var fileName = System.IO.Path.GetFileName(sel.FilePath);

        var all = ErrorListReader.Read();
        var forFile = all.FirstOrDefault(kv => string.Equals(kv.Key, sel.FilePath, StringComparison.OrdinalIgnoreCase)).Value;
        if (forFile is null || forFile.Count == 0)
        {
            Log.Info($"Fix Errors: the Error List has no entries for {fileName}. Build the project if you expected some.");
            return;
        }

        // Prefer the selection's line range; fall back to the whole file when the range has none.
        var diags = forFile.Cast<Newtonsoft.Json.Linq.JObject>().ToList();
        bool scopedToSelection = false;
        if (!sel.IsEmpty)
        {
            var inRange = diags.Where(d =>
            {
                int line = (int?)d["range"]?["start"]?["line"] ?? -1;
                return line >= sel.StartLine && line <= sel.EndLineInclusive;
            }).ToList();
            if (inRange.Count > 0) { diags = inRange; scopedToSelection = true; }
        }

        bool launched = EnsureSessionLaunching("Fix Errors");
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Fix these compiler diagnostics in {rel}{(scopedToSelection ? $" (selection, lines {sel.StartLine + 1}-{sel.EndLineInclusive + 1})" : "")}:");
        const int cap = 30;
        foreach (var d in diags.Take(cap))
        {
            int line = (int?)d["range"]?["start"]?["line"] ?? 0;
            string sev = (int?)d["severity"] switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" };
            sb.AppendLine($"  {sev} (line {line + 1}): {(string?)d["message"]}");
        }
        if (diags.Count > cap) sb.AppendLine($"  …and {diags.Count - cap} more.");
        sb.AppendLine();
        sb.AppendLine("The file is mentioned alongside this note. Make the smallest correct fix, then verify with getDiagnostics that these are gone.");

        await Attachments.AttachmentService.StageTextAsync(sb.ToString(), $"fix-{fileName}");
        await SelectionService.MentionAsync(sel.FilePath,
            scopedToSelection ? (int?)sel.StartLine : null,
            scopedToSelection ? (int?)sel.EndLineInclusive : null, "Fix Errors");
        FocusClaude(launched);
    }

    // ---- Generate Documentation / Add Comments: the function at the caret -------------------------

    public static Task GenerateDocsAsync() => FunctionActionAsync(
        action: "Generate Documentation",
        stem: "docs",
        headerForFunction: (name, rel, a, b) =>
            $"Add documentation comments for `{name}` in {rel} (lines {a}-{b}, mentioned alongside this note). " +
            "Match the file's existing documentation style (for C#: /// XML docs - <summary>, plus <param>/<returns> where they add real information, not restatements). " +
            "Edit the file; do not change the code itself.",
        headerForSelection: (rel, a, b) =>
            $"Add documentation comments for the code in {rel} (lines {a}-{b}, mentioned alongside this note). " +
            "Match the file's existing documentation style. Edit the file; do not change the code itself.");

    public static Task AddCommentsAsync() => FunctionActionAsync(
        action: "Add Comments",
        stem: "comments",
        headerForFunction: (name, rel, a, b) =>
            $"Add explanatory comments inside `{name}` in {rel} (lines {a}-{b}, mentioned alongside this note) - " +
            "but ONLY where the code is not self-explanatory: magic numbers, non-obvious constants, invariants, and why-this-way choices. " +
            "Match the file's comment style, never narrate obvious lines, and do not change the code itself.",
        headerForSelection: (rel, a, b) =>
            $"Add explanatory comments to the code in {rel} (lines {a}-{b}, mentioned alongside this note) - " +
            "but ONLY where it is not self-explanatory: magic numbers, non-obvious constants, invariants, and why-this-way choices. " +
            "Match the file's comment style, never narrate obvious lines, and do not change the code itself.");

    private static async Task FunctionActionAsync(
        string action, string stem,
        Func<string, string, int, int, string> headerForFunction,
        Func<string, int, int, string> headerForSelection)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn($"{action}: no active file.");
            return;
        }
        var fileName = System.IO.Path.GetFileName(sel.FilePath);
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;

        // Roslyn resolves the enclosing function from the caret; hops off the UI thread itself.
        var fn = await CodeModel.RoslynReader.GetEnclosingFunctionAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);

        if (fn != null)
        {
            bool launched = EnsureSessionLaunching(action);
            int a = fn.StartLine + 1, b = fn.EndLine + 1;
            await Attachments.AttachmentService.StageTextAsync(
                headerForFunction(fn.DisplayName, rel, a, b), $"{stem}-{fileName}-L{a}-{b}");
            await SelectionService.MentionAsync(sel.FilePath, fn.StartLine, fn.EndLine, action);
            FocusClaude(launched);
            return;
        }

        if (!sel.IsEmpty)
        {
            // No Roslyn (loose file, C++, no project) but the user told us the range themselves.
            bool launched = EnsureSessionLaunching(action);
            int a = sel.StartLine + 1, b = sel.EndLineInclusive + 1;
            await Attachments.AttachmentService.StageTextAsync(
                headerForSelection(rel, a, b), $"{stem}-{fileName}-L{a}-{b}");
            await SelectionService.MentionAsync(sel.FilePath, sel.StartLine, sel.EndLineInclusive, action);
            FocusClaude(launched);
            return;
        }

        Log.Info($"{action}: place the caret inside a function (needs a loaded C#/VB project), or select the code first.");
    }

    // ---- Fix This Test: run/debug/catch the test at the caret -------------------------------------

    public static async Task FixTestAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Fix This Test: no active file.");
            return;
        }

        var fn = await CodeModel.RoslynReader.GetEnclosingFunctionAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);
        if (fn is null)
        {
            Log.Info("Fix This Test: place the caret inside a test method (needs a loaded C#/VB project).");
            return;
        }
        if (!fn.IsTest)
        {
            Log.Info($"Fix This Test: '{fn.DisplayName}' carries no test attribute ([Fact]/[Theory]/[Test]/[TestMethod]).");
            return;
        }

        bool launched = EnsureSessionLaunching("Fix This Test");
        var header =
            $"Run the test {fn.FullyQualifiedName} with the vs-debug tool vs_run_test. " +
            "If it fails, investigate and fix whichever is wrong - the code or the test: vs_debug_test stops at the throw with the exception and locals in view, " +
            "and vs_catch_flaky catches an intermittent failure red-handed (both need the 'Allow Claude to drive debugger' toggle in the Claude Code panel). " +
            "Re-run vs_run_test afterward to verify it passes. The test's source is mentioned alongside this note.";
        await Attachments.AttachmentService.StageTextAsync(header, $"fixtest-{fn.FullyQualifiedName.Split('.').Last()}");
        await SelectionService.MentionAsync(sel.FilePath, fn.StartLine, fn.EndLine, "Fix This Test");
        FocusClaude(launched);
    }

    // ---- Generate Tests: the function at the caret (or the selection) -> tests in the right project ---

    /// <summary>
    /// Ask for tests for the member at the caret, composed with what the solution already has: the test
    /// project(s) and their framework (so the tests land where they belong, in the house style), whether
    /// that project already references the code under test, and the tests that already exercise this
    /// member (extend, don't duplicate). Then the verify loop: vs_build + vs_run_test on the new tests.
    /// </summary>
    public static async Task GenerateTestsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sel = SelectionService.Current;
        if (sel.FilePath is null)
        {
            Log.Warn("Generate Tests: no active file.");
            return;
        }
        var fileName = System.IO.Path.GetFileName(sel.FilePath);
        var rel = Attachments.AttachmentService.ToWorkspaceRelative(sel.FilePath) ?? fileName;

        var fn = await CodeModel.RoslynReader.GetEnclosingFunctionAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);
        if (fn == null && sel.IsEmpty)
        {
            Log.Info("Generate Tests: place the caret inside a method (needs a loaded C#/VB project), or select the code first.");
            return;
        }
        if (fn is { IsTest: true })
        {
            Log.Info($"Generate Tests: '{fn.DisplayName}' is itself a test - use Fix This Test on it, or put the caret in the code under test.");
            return;
        }

        var ctx = await CodeModel.RoslynReader.GetTestContextAsync(
            sel.FilePath, sel.StartLine, sel.StartChar, CancellationToken.None);

        bool launched = EnsureSessionLaunching("Generate Tests");
        int a = (fn?.StartLine ?? sel.StartLine) + 1;
        int b = (fn?.EndLine ?? sel.EndLineInclusive) + 1;
        var subject = fn != null ? $"`{fn.DisplayName}`" : "the selected code";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Write unit tests for {subject} in {rel} (lines {a}-{b}, mentioned alongside this note).");
        sb.AppendLine();

        // Where the tests go. Prefer a test project that already references the code under test.
        var home = ctx.TestProjects.FirstOrDefault(p => p.ReferencesTarget) ?? ctx.TestProjects.FirstOrDefault();
        if (home != null)
        {
            sb.AppendLine($"Put them in the existing test project {home.Name} ({home.Framework}"
                        + (home.Path != null ? $", {home.Path}" : "") + ").");
            if (!home.ReferencesTarget && ctx.TargetProject != null)
                sb.AppendLine($"It does not reference {ctx.TargetProject} yet - add the project reference.");
            if (ctx.TestProjects.Count > 1)
                sb.AppendLine("Other test projects in the solution: "
                            + string.Join(", ", ctx.TestProjects.Where(p => p != home).Select(p => $"{p.Name} ({p.Framework})")) + ".");
            sb.AppendLine("Follow that project's existing conventions: file and class naming, fixtures, assertion style, and how test cases are parameterized.");
        }
        else
        {
            sb.AppendLine("The solution has no test project. Propose one (xUnit unless the repo already leans another way) and confirm with me before creating it.");
        }

        if (ctx.ExistingTests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Tests that already exercise this code - extend or complement them, don't duplicate:");
            foreach (var t in ctx.ExistingTests) sb.AppendLine($"  - {t}");
        }

        sb.AppendLine();
        sb.AppendLine("Cover the behavior this code actually has: the normal path, the boundaries, and each failure mode it handles. Don't test the framework or restate the implementation.");
        sb.AppendLine("Then verify with the vs-debug tools: vs_build the test project, run the new tests with vs_run_test, and fix whatever fails. "
                    + "If a test exposes a real bug in the code under test, say so instead of bending the test to pass.");

        await Attachments.AttachmentService.StageTextAsync(sb.ToString(), $"tests-{fileName}-L{a}-{b}");
        await SelectionService.MentionAsync(sel.FilePath, a - 1, b - 1, "Generate Tests");
        FocusClaude(launched);
    }

    // ---- shared ------------------------------------------------------------------------------------

    /// <summary>
    /// Official-extension parity: an action with no session running launches one instead of failing.
    /// Fire-and-forget - staged .txt attachments deliver on connect via the tray's queued mentions;
    /// range mentions pushed before the connect may need the chip's click-to-re-mention.
    /// Returns true when a launch was fired (callers then skip the explicit refocus - the fresh
    /// terminal takes focus itself).
    /// </summary>
    private static bool EnsureSessionLaunching(string action)
    {
        if (Ui.BridgeStatus.Connected) return false;
        var launch = Ui.BridgeStatus.LaunchAction;
        if (launch is null) return false;
        Log.Info($"{action}: no Claude session - launching one. Staged items deliver when it connects.");
        _ = launch();
        return true;
    }

    /// <summary>
    /// Focus the claude session's input so Enter sends what the action just pushed (the #33
    /// focus-trap, solved). Skipped when the action just launched a session - the fresh terminal
    /// takes focus on its own.
    /// </summary>
    private static void FocusClaude(bool launchedJustNow)
    {
        if (launchedJustNow) return;
        var focus = Ui.BridgeStatus.FocusClaudeAction;
        if (focus != null) _ = focus();
    }
}
