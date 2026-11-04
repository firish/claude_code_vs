using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using ClaudeCodeVs.Ui;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// VS package entry point. Auto-loads when the shell finishes initializing (so the bridge server is
/// up regardless of whether a solution is open), runs everything on a background-loadable async init,
/// and registers the "Launch Claude Code" command. See build-plan.md §2.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
// Register the extension folder as an assembly probe path. Our package assembly isn't strong-named,
// so the generated pkgdef uses "Assembly=" with no CodeBase; without a binding path, devenv's
// load-by-name (Activator.CreateInstance) can't locate ClaudeCodeVS.dll / ClaudeCodeVS.Protocol.dll
// and the package fails with "Could not load file or assembly 'ClaudeCodeVS' ...". This fixes that.
[ProvideBindingPath]
[ProvideMenuResource("Menus.ctmenu", 1)] // the compiled VSCommandTable.vsct
[ProvideToolWindow(typeof(ClaudeToolWindow))] // the dockable "Claude Code" panel
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class ClaudeCodeVsPackage : AsyncPackage
{
    private BridgeHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // UI language BEFORE anything renders: Strings.Culture follows VS's own display language
        // (Tools > Options > International Settings), read from the shell rather than the thread
        // culture - UI strings get composed on background HTTP-handler threads too. Runs before the
        // tool window can exist (VS finishes package init before creating its tool windows).
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        InitUiCulture();
        await TaskScheduler.Default; // hop back off the UI thread for server startup

        _host = new BridgeHost(this);
        await _host.StartAsync(cancellationToken);

        // Register the Tools -> "Launch Claude Code" and "Claude Code Panel" commands.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
        {
            mcs.AddCommand(new MenuCommand(OnLaunchClaude,
                new CommandID(PackageGuids.CommandSet, PackageIds.LaunchClaude)));
            mcs.AddCommand(new MenuCommand(OnShowPanel,
                new CommandID(PackageGuids.CommandSet, PackageIds.ShowPanel)));
            mcs.AddCommand(new MenuCommand(OnExplain,
                new CommandID(PackageGuids.CommandSet, PackageIds.Explain)));
            mcs.AddCommand(new MenuCommand(OnAddToChat,
                new CommandID(PackageGuids.CommandSet, PackageIds.AddToChat)));
            mcs.AddCommand(new MenuCommand(OnFixErrors,
                new CommandID(PackageGuids.CommandSet, PackageIds.FixErrors)));
            mcs.AddCommand(new MenuCommand(OnGenerateDocs,
                new CommandID(PackageGuids.CommandSet, PackageIds.GenerateDocs)));
            mcs.AddCommand(new MenuCommand(OnAddComments,
                new CommandID(PackageGuids.CommandSet, PackageIds.AddComments)));
            mcs.AddCommand(new MenuCommand(OnAddItemsToChat,
                new CommandID(PackageGuids.CommandSet, PackageIds.AddItemsToChat)));

            // Fix This Test hides outside test-looking files; Generate Tests hides INSIDE them (a test
            // file is not what you generate tests for). The visibility check must be instant (it runs
            // on every right-click), so it's a filename heuristic - "test" in the name - not a Roslyn
            // query; the real resolution happens at click time.
            var fixTest = new OleMenuCommand(OnFixTest,
                new CommandID(PackageGuids.CommandSet, PackageIds.FixTest));
            fixTest.BeforeQueryStatus += (s, _) => { if (s is OleMenuCommand cmd) cmd.Visible = ActiveFileLooksLikeTest(); };
            mcs.AddCommand(fixTest);

            var generateTests = new OleMenuCommand(OnGenerateTests,
                new CommandID(PackageGuids.CommandSet, PackageIds.GenerateTests));
            generateTests.BeforeQueryStatus += (s, _) => { if (s is OleMenuCommand cmd) cmd.Visible = !ActiveFileLooksLikeTest(); };
            mcs.AddCommand(generateTests);
        }

        // Once-per-marquee-release "what's new" InfoBar. No-op on routine releases
        // (UpdateNotice.MarqueeVersion is null) and after the user has seen an armed one.
        JoinableTaskFactory.RunAsync(Ui.UpdateNotice.ShowOnceAsync).FileAndForget("claudecodevs/updateNotice");
    }

    /// <summary>"test" anywhere in the active file's name - the menu-open heuristic shared by the two test entries.</summary>
    private static bool ActiveFileLooksLikeTest()
    {
        var file = Editor.SelectionService.Current.FilePath;
        return file != null
            && System.IO.Path.GetFileNameWithoutExtension(file).IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Point <see cref="Strings.Culture"/> at VS's display-language LCID (2052 = zh-CN resolves the
    /// zh-Hans satellite via the standard parent-chain fallback; anything untranslated falls back to
    /// English per-string). Any failure leaves Culture null = neutral English.
    /// </summary>
    private void InitUiCulture()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (GetService(typeof(SUIHostLocale)) is IUIHostLocale hostLocale
                && ErrorHandler.Succeeded(hostLocale.GetUILocale(out uint lcid))
                && lcid > 0)
            {
                Strings.Culture = new System.Globalization.CultureInfo((int)lcid);
            }
        }
        catch (Exception ex)
        {
            Protocol.Log.Warn($"UI culture detection failed, staying English: {ex.Message}");
        }
    }

    private void OnLaunchClaude(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var host = _host;
        if (host is null) return;
        JoinableTaskFactory.RunAsync(() => host.LaunchClaudeAsync()).FileAndForget("claudecodevs/launch");
        // Tools is the only entry a first-run user finds, so show the panel too - one click starts a
        // session AND surfaces the toggles/stats. After the launch: a panel that fails to open mustn't
        // take the launch down with it. (The panel's own Launch button doesn't route here.)
        ShowPanel();
    }

    private void OnShowPanel(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ShowPanel();
    }

    private void ShowPanel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = FindToolWindow(typeof(ClaudeToolWindow), 0, create: true);
        if (window?.Frame is IVsWindowFrame frame)
            ErrorHandler.ThrowOnFailure(frame.Show());
    }

    // Editor right-click ("Claude Code" flyout): the action bodies live in Editor/ContextActions -
    // each stages a self-describing instruction .txt and @-mentions the code it talks about
    // (insert-not-submit; the composer receives the references, the user sends).
    private void OnExplain(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.ExplainAsync).FileAndForget("claudecodevs/explain");

    private void OnAddToChat(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.AddToChatAsync).FileAndForget("claudecodevs/addToChat");

    private void OnFixErrors(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.FixErrorsAsync).FileAndForget("claudecodevs/fixErrors");

    private void OnGenerateDocs(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.GenerateDocsAsync).FileAndForget("claudecodevs/generateDocs");

    private void OnAddComments(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.AddCommentsAsync).FileAndForget("claudecodevs/addComments");

    // Solution Explorer right-click: @-mention the selected files/folders (whole-file, multi-select).
    private void OnAddItemsToChat(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.AddSelectionToChatAsync).FileAndForget("claudecodevs/addItemsToChat");

    private void OnFixTest(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.FixTestAsync).FileAndForget("claudecodevs/fixTest");

    private void OnGenerateTests(object sender, EventArgs e)
        => JoinableTaskFactory.RunAsync(Editor.ContextActions.GenerateTestsAsync).FileAndForget("claudecodevs/generateTests");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class PackageGuids
{
    public const string PackageString = "d9032717-8a83-4ab5-9b63-2fe9d9a78481";
    // Must match guidClaudeCmdSet in VSCommandTable.vsct.
    public static readonly Guid CommandSet = new Guid("9495bbbb-756d-4dc4-807d-1408d50e7d33");
}

internal static class PackageIds
{
    // Must match the IDSymbol values in VSCommandTable.vsct.
    public const int LaunchClaude = 0x0100;
    public const int ShowPanel = 0x0101;
    public const int Explain = 0x0102;
    public const int AddToChat = 0x0103;
    public const int FixErrors = 0x0104;
    public const int GenerateDocs = 0x0105;
    public const int AddComments = 0x0106;
    public const int FixTest = 0x0107;
    public const int AddItemsToChat = 0x0108;
    public const int GenerateTests = 0x0109;
}
