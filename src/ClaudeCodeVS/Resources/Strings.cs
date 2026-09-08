using System.Globalization;
using System.Resources;

namespace ClaudeCodeVs;

/// <summary>
/// User-facing UI strings (issue #20: Simplified Chinese language pack). Every string a user SEES -
/// panel chrome, dialogs, the diff InfoBar, notifications - resolves through here; Strings.resx is
/// the neutral English, Strings.zh-Hans.resx the Chinese satellite. Untranslated keys fall back
/// per-string to English, so a release whose new strings aren't translated yet degrades to a mixed
/// UI, never a broken one. NOT localized by design: log/feed diagnostics (greppable bug reports),
/// anything sent to the CLI or model (protocol strings, tool results, hook scripts), and the
/// "Claude Code" brand name itself.
///
/// The properties are hand-written (no ResXFileCodeGenerator - the custom tool only runs inside the
/// IDE, and this repo builds from the command line): key name == property name via nameof, and a
/// missing resource returns the key instead of throwing so the UI never dies on a resx gap.
/// <see cref="Culture"/> is set once at package init from VS's own display language
/// (VSSPROPID_Locale), NOT the thread culture - strings are composed on background HTTP-handler
/// threads too.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Rm =
        new ResourceManager("ClaudeCodeVs.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>VS's display language; null until package init (falls back to neutral English).</summary>
    internal static CultureInfo? Culture { get; set; }

    private static string S(string key)
    {
        try { return Rm.GetString(key, Culture) ?? key; }
        catch { return key; }
    }

    // ---- Panel: toolbar buttons ----
    internal static string BtnClear => S(nameof(BtnClear));
    internal static string BtnOutput => S(nameof(BtnOutput));
    internal static string BtnLaunch => S(nameof(BtnLaunch));
    internal static string BtnExternalConsole => S(nameof(BtnExternalConsole));
    internal static string TipExternalConsole => S(nameof(TipExternalConsole));
    internal static string BtnRelaunch => S(nameof(BtnRelaunch));

    // ---- Panel: toggles ----
    internal static string ToggleAutoAccept => S(nameof(ToggleAutoAccept));
    internal static string TipAutoAccept => S(nameof(TipAutoAccept));
    internal static string TipAutoAcceptLocked => S(nameof(TipAutoAcceptLocked));
    internal static string ToggleAllowDrive => S(nameof(ToggleAllowDrive));
    internal static string TipAllowDrive => S(nameof(TipAllowDrive));
    internal static string ToggleAllowCapture => S(nameof(ToggleAllowCapture));
    internal static string TipAllowCapture => S(nameof(TipAllowCapture));
    internal static string ToggleNotify => S(nameof(ToggleNotify));
    internal static string TipNotify => S(nameof(TipNotify));

    // ---- Panel: status pill + endpoint ----
    internal static string StatusStarting => S(nameof(StatusStarting));
    internal static string StatusConnected => S(nameof(StatusConnected));
    internal static string StatusWaiting => S(nameof(StatusWaiting));
    internal static string EndpointFormat => S(nameof(EndpointFormat));
    internal static string NoWorkspace => S(nameof(NoWorkspace));

    // ---- Panel: stats card ----
    internal static string StatsEdits => S(nameof(StatsEdits));
    internal static string StatsDebug => S(nameof(StatsDebug));
    internal static string StatsLatest => S(nameof(StatsLatest));
    internal static string StatsSession => S(nameof(StatsSession));
    internal static string StatsTurns => S(nameof(StatsTurns));
    internal static string BtnShowCost => S(nameof(BtnShowCost));
    internal static string BtnHideCost => S(nameof(BtnHideCost));
    internal static string CostFormat => S(nameof(CostFormat));
    internal static string PendingFormat => S(nameof(PendingFormat));
    internal static string FeedLabel => S(nameof(FeedLabel));

    // ---- Panel: the "Edits this turn" jump list (issue #44) ----
    internal static string EditsLabel => S(nameof(EditsLabel));
    internal static string EditsCountFormat => S(nameof(EditsCountFormat));
    internal static string EditsRewrittenFormat => S(nameof(EditsRewrittenFormat));
    internal static string EditsTooltipFormat => S(nameof(EditsTooltipFormat));
    internal static string EditsTooltipRewritten => S(nameof(EditsTooltipRewritten));

    // ---- Panel: warning banners ----
    internal static string BannerHooksOnlyTitle => S(nameof(BannerHooksOnlyTitle));
    internal static string BannerHooksOnlyText => S(nameof(BannerHooksOnlyText));
    internal static string BannerConfigTitle => S(nameof(BannerConfigTitle));
    internal static string BannerConfigText => S(nameof(BannerConfigText));

    // ---- Panel: attachments ----
    internal static string AttachHint => S(nameof(AttachHint));
    internal static string BtnPaste => S(nameof(BtnPaste));
    internal static string TipPaste => S(nameof(TipPaste));
    internal static string BtnCompose => S(nameof(BtnCompose));
    internal static string TipCompose => S(nameof(TipCompose));
    internal static string AttachSummaryFormat => S(nameof(AttachSummaryFormat));
    internal static string ChipEstimateFormat => S(nameof(ChipEstimateFormat));
    internal static string ChipNeedsTool => S(nameof(ChipNeedsTool));
    internal static string ChipClickRemention => S(nameof(ChipClickRemention));
    internal static string ChipStagedRetry => S(nameof(ChipStagedRetry));
    internal static string ChipRemoveCopied => S(nameof(ChipRemoveCopied));
    internal static string ChipRemove => S(nameof(ChipRemove));

    // ---- Compose dialog ----
    internal static string ComposeTitle => S(nameof(ComposeTitle));
    internal static string ComposePrompt => S(nameof(ComposePrompt));
    internal static string BtnAttach => S(nameof(BtnAttach));
    internal static string BtnCancel => S(nameof(BtnCancel));
    internal static string TokensWhenRead => S(nameof(TokensWhenRead));

    // ---- Reason dialog ----
    internal static string ReasonTitle => S(nameof(ReasonTitle));
    internal static string ReasonPrompt => S(nameof(ReasonPrompt));
    internal static string BtnSendToClaude => S(nameof(BtnSendToClaude));
    internal static string BtnJustReject => S(nameof(BtnJustReject));

    // ---- Diff InfoBar ----
    internal static string DiffAccept => S(nameof(DiffAccept));
    internal static string DiffReject => S(nameof(DiffReject));
    internal static string DiffRejectWithReason => S(nameof(DiffRejectWithReason));
    internal static string DiffProposes => S(nameof(DiffProposes));
    internal static string DiffLeftLabel => S(nameof(DiffLeftLabel));
    internal static string DiffRightLabel => S(nameof(DiffRightLabel));

    // ---- Notifications ----
    internal static string MarqueeNoticeText => S(nameof(MarqueeNoticeText));
    internal static string ReleaseNotesLink => S(nameof(ReleaseNotesLink));
    internal static string NotifyTurnEnded => S(nameof(NotifyTurnEnded));
    internal static string NotifyNeedsInput => S(nameof(NotifyNeedsInput));
    internal static string TipRunWildOn => S(nameof(TipRunWildOn));
}
