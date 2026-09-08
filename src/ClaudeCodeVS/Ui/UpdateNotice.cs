using System;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The once-per-marquee-release "what's new" notice: a single sticky InfoBar with a Release-notes
/// link, shown the first time VS starts on an ARMED version and never again (latched in the VS
/// user-settings store, which survives VS restarts and extension updates).
///
/// Arming is an editorial act, NOT part of every release: most releases leave
/// <see cref="MarqueeVersion"/> null and ship silently (update toasts breed resentment; the
/// Marketplace listing and GitHub releases carry routine notes). A marquee release - something that
/// changes behavior users rely on, or a headline feature worth one interruption - arms it by:
///   1. setting <see cref="MarqueeVersion"/> to that release's version string, and
///   2. rewriting <c>MarqueeNoticeText</c> in Strings.resx AND Strings.zh-Hans.resx (convention #6 -
///      Claude translates) with that release's one-sentence blurb.
/// Disarm (null) again on the next routine release.
/// </summary>
internal static class UpdateNotice
{
    /// <summary>Null = this release ships silently. Set to the release version to arm (see class doc).</summary>
    private static readonly string? MarqueeVersion = "1.21.0"; // ARMED: jump-to-edit + the build/output tools

    private const string ReleaseNotesUrl = "https://github.com/firish/claude_code_vs/releases";
    private const string Collection = "ClaudeCodeVS";
    private const string LastShownKey = "LastMarqueeNoticeShown";

    /// <summary>
    /// Show the notice if this version is armed and this user hasn't seen it. The latch is written
    /// ONLY after the InfoBar actually rendered, so a too-early startup (no InfoBar host yet) retries
    /// on the next VS start instead of silently burning the one showing.
    /// </summary>
    public static async Task ShowOnceAsync()
    {
        if (string.IsNullOrEmpty(MarqueeVersion)) return;
        try
        {
            // Let the shell finish composing its main window before asking for the InfoBar host.
            await Task.Delay(TimeSpan.FromSeconds(5));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var store = new ShellSettingsManager(ServiceProvider.GlobalProvider)
                .GetWritableSettingsStore(SettingsScope.UserSettings);
            if (store.CollectionExists(Collection)
                && store.GetString(Collection, LastShownKey, "") == MarqueeVersion)
                return; // already seen this marquee

            bool shown = await Notifier.AnnounceAsync(Strings.MarqueeNoticeText, Strings.ReleaseNotesLink, ReleaseNotesUrl);
            if (!shown) return;

            if (!store.CollectionExists(Collection)) store.CreateCollection(Collection);
            store.SetString(Collection, LastShownKey, MarqueeVersion!);
        }
        catch (Exception e)
        {
            Log.Warn($"update notice failed (harmless): {e.Message}");
        }
    }
}
