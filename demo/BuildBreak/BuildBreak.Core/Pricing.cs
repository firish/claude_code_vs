namespace BuildBreak.Core;

/// <summary>
/// Fixture for vs_build. This project is DELIBERATELY broken: one compile error and one warning, so a
/// build reports errorCount 1 / warningCount 1 with the error attributed to BuildBreak.Core while the
/// sibling BuildBreak.App still builds (projectsFailed 1, not 2).
///
/// To exercise the loop: vs_build -> read the error -> fix Describe -> vs_build -> ok:true.
/// </summary>
public static class Pricing
{
    public static decimal WithTax(decimal amount, decimal ratePercent)
    {
        decimal scratch = 0m; // CS0219: assigned but never used - the warning half of the fixture
        return amount + amount * (ratePercent / 100m);
    }

    public static string Describe(decimal amount)
    {
        // CS0029: cannot implicitly convert type 'decimal' to 'string'.
        // The fix is amount.ToString("0.00") - small enough that the fix-verify loop is one round trip.
        string label = amount;
        return label;
    }
}
