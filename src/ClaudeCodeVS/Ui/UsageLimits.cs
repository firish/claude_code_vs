using System;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Ui;

/// <summary>One usage window: how much of it is used, and when it resets (null when the CLI didn't say).</summary>
internal sealed class UsageWindow
{
    public double Percent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>A window whose reset time has passed no longer describes anything (the CLI drops those too).</summary>
    public bool IsCurrent => ResetsAt == null || ResetsAt > DateTimeOffset.UtcNow;
}

/// <summary>
/// The subscription usage the CLI reports in its status-line data (<c>rate_limits</c>): the rolling
/// 5-hour window, the weekly window, and - behind a Claude apps gateway - a spend limit. Each is
/// independently optional; the object is only present for Pro/Max/Ultra (or gateway) sessions, and only
/// after the session's first API response.
/// </summary>
internal sealed class UsageLimits
{
    public UsageWindow? FiveHour { get; set; }
    public UsageWindow? SevenDay { get; set; }
    public UsageWindow? Spend { get; set; }

    public bool HasCurrent =>
        FiveHour?.IsCurrent == true || SevenDay?.IsCurrent == true || Spend?.IsCurrent == true;

    /// <summary>Null when <paramref name="rateLimits"/> carries no usable window.</summary>
    public static UsageLimits? Parse(JObject rateLimits)
    {
        var limits = new UsageLimits
        {
            FiveHour = Window(rateLimits["five_hour"]),
            SevenDay = Window(rateLimits["seven_day"]),
            Spend = Window(rateLimits["spend_limit"]),
        };
        return limits.FiveHour == null && limits.SevenDay == null && limits.Spend == null ? null : limits;
    }

    private static UsageWindow? Window(JToken? token)
    {
        if (token is not JObject o) return null;
        var percent = o["used_percentage"];
        if (percent == null || percent.Type is not (JTokenType.Integer or JTokenType.Float)) return null;

        DateTimeOffset? resets = null;
        var resetsAt = o["resets_at"];
        if (resetsAt != null && resetsAt.Type is JTokenType.Integer or JTokenType.Float)
        {
            try { resets = DateTimeOffset.FromUnixTimeSeconds((long)(double)resetsAt); }
            catch { /* out of range: treat as unknown */ }
        }
        return new UsageWindow { Percent = Math.Max(0, Math.Min(100, (double)percent)), ResetsAt = resets };
    }
}
