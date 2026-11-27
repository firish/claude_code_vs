using System;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// Panel preferences that survive a VS restart, kept per user in VS's own settings store (the same
/// "ClaudeCodeVS" collection <see cref="UpdateNotice"/> uses). Only convenience preferences live here: the
/// safety toggles (run wild, debugger drive, screen capture) deliberately reset every session.
/// Best-effort - a settings-store failure falls back to the default and is never surfaced. UI thread.
/// </summary>
internal static class PanelSettings
{
    private const string Collection = "ClaudeCodeVS";
    public const string NotifyKey = "NotifyEnabled";
    public const string FeedWrapKey = "ActivityWrap";
    public const string FeedTimestampsKey = "ActivityTimestamps";
    public const string UsageStatusLineKey = "UsageStatusLine";
    public const string FeedWarningsOnlyKey = "ActivityWarningsOnly";
    public const string SelectedTabKey = "SelectedTab";
    public const string TopHeightKey = "TopSectionHeight"; // "" = automatic

    public static string GetString(string key, string fallback)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var store = Store();
            return store.CollectionExists(Collection) && store.PropertyExists(Collection, key)
                ? store.GetString(Collection, key)
                : fallback;
        }
        catch { return fallback; }
    }

    public static void SetString(string key, string value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var store = Store();
            if (!store.CollectionExists(Collection)) store.CreateCollection(Collection);
            store.SetString(Collection, key, value);
        }
        catch (Exception e)
        {
            Log.Event($"panel settings: couldn't save {key} ({e.Message})");
        }
    }

    public static bool GetBool(string key, bool fallback)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var store = Store();
            return store.CollectionExists(Collection) && store.PropertyExists(Collection, key)
                ? store.GetBoolean(Collection, key)
                : fallback;
        }
        catch (Exception e)
        {
            Log.Event($"panel settings: couldn't read {key} ({e.Message})");
            return fallback;
        }
    }

    public static void SetBool(string key, bool value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var store = Store();
            if (!store.CollectionExists(Collection)) store.CreateCollection(Collection);
            store.SetBoolean(Collection, key, value);
        }
        catch (Exception e)
        {
            Log.Event($"panel settings: couldn't save {key} ({e.Message})");
        }
    }

    private static WritableSettingsStore Store() =>
        new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
}
