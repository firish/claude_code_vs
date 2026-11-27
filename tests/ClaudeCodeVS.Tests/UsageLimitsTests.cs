using System;
using ClaudeCodeVs.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tests;

[TestClass]
public class UsageLimitsTests
{
    private static readonly long Future = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
    private static readonly long Past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

    [TestMethod]
    public void ParsesBothWindows()
    {
        var limits = UsageLimits.Parse(JObject.Parse(
            $"{{\"five_hour\":{{\"used_percentage\":42.4,\"resets_at\":{Future}}},\"seven_day\":{{\"used_percentage\":81,\"resets_at\":{Future}}}}}"));

        Assert.IsNotNull(limits);
        Assert.AreEqual(42.4, limits!.FiveHour!.Percent, 0.001);
        Assert.AreEqual(81, limits.SevenDay!.Percent, 0.001);
        Assert.IsNull(limits.Spend);
        Assert.IsTrue(limits.HasCurrent);
    }

    [TestMethod]
    public void NoUsableWindow_IsNull()
    {
        Assert.IsNull(UsageLimits.Parse(JObject.Parse("{}")));
        Assert.IsNull(UsageLimits.Parse(JObject.Parse("{\"five_hour\":{\"resets_at\":1}}")));
        Assert.IsNull(UsageLimits.Parse(JObject.Parse("{\"five_hour\":{\"used_percentage\":\"lots\"}}")));
    }

    [TestMethod]
    public void PercentIsClamped_AndMissingResetIsAllowed()
    {
        var limits = UsageLimits.Parse(JObject.Parse("{\"seven_day\":{\"used_percentage\":130}}"))!;

        Assert.AreEqual(100, limits.SevenDay!.Percent);
        Assert.IsNull(limits.SevenDay.ResetsAt);
        Assert.IsTrue(limits.SevenDay.IsCurrent);
    }

    [TestMethod]
    public void WindowPastItsReset_IsNotCurrent()
    {
        var limits = UsageLimits.Parse(JObject.Parse($"{{\"five_hour\":{{\"used_percentage\":10,\"resets_at\":{Past}}}}}"))!;

        Assert.IsFalse(limits.FiveHour!.IsCurrent);
        Assert.IsFalse(limits.HasCurrent);
    }
}
