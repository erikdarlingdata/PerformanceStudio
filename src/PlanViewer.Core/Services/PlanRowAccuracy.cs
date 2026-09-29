using System;
using System.Globalization;

namespace PlanViewer.Core.Services;

/// <summary>
/// The plan viewer's node row line: the rows an operator actually returned against the rows it was
/// expected to, printed as <c>"{actual} of {expected} ({percent}%)"</c>. The expected figure is
/// <see cref="RowEstimateHelper.GetExpectedRows"/>, a total like <c>ActualRows</c> (#594). Shared by
/// the App and Web node labels and the HTML export (#611). PerformanceMonitor's plan viewer ports
/// the same rule (its <c>PlanRowAccuracy</c>, #4684), so both apps print the same string for the
/// same plan.
/// </summary>
public static class PlanRowAccuracy
{
    /// <summary>The most decimals <see cref="PrintActualOfExpected"/> adds to make a label agree with its percentage.</summary>
    private const int MaxAgreementDecimals = 4;

    /// <summary>
    /// The node row line: <c>"{actual} of {expected} ({percent}%)"</c>, or just <c>"{actual} of {expected}"</c>
    /// when <paramref name="expectedRows"/> is not above zero. See <see cref="PrintActualOfExpected"/> for the rule.
    /// A Key Lookup that ran 117 times for 1 row (estimate 0.00964372 each) reads <c>1 of 1.128 (89%)</c>, not the
    /// <c>1 of 1 (89%)</c> that printing both counts <c>N0</c> gave.
    /// </summary>
    public static string FormatActualOfExpected(double actualRows, double expectedRows, IFormatProvider? provider = null)
    {
        var (actual, expected, percent) = PrintActualOfExpected(actualRows, expectedRows, provider);
        return percent == null
            ? string.Concat(actual, " of ", expected)
            : string.Concat(actual, " of ", expected, " (", percent, "%)");
    }

    /// <summary>
    /// The two row counts and the whole percentage that <see cref="FormatActualOfExpected"/> joins, for a surface
    /// that words the line its own way (the HTML export adds "rows"). The rule, word for word:
    /// <i>Print N0. If the whole percent computed from the printed numbers differs from the printed percentage, add the
    /// fewest decimals (fixed-point, never scientific, capped at 4) at which they agree. A non-zero value never prints as
    /// zero. If it would, print it fixed-point to its first significant digit.</i>
    /// <list type="bullet">
    /// <item>The percentage is <c>actualRows / expectedRows * 100</c> to a whole number, from the unrounded values. It is
    /// null when <paramref name="expectedRows"/> is not above zero (there is nothing to divide by).</item>
    /// <item>The agreement search runs from 0 to 4 decimals. At each count a whole number prints <c>N0</c> and any other
    /// number prints <c>N</c> plus that count. The text is parsed back (group separator allowed, so "2,983" reads as
    /// 2983) and the search stops at the first count where the whole percent of the printed numbers equals the printed
    /// percentage. A printed divisor of zero never agrees. At 4 it stops either way, so a value like 0.00012345 can
    /// print a line that still contradicts its percentage: the cap wins.</item>
    /// <item>A non-zero value whose text would read as zero is reprinted with as many decimals as it takes to show its
    /// first significant digit (0.000005 prints "0.000005"); the cap does not apply to that.</item>
    /// <item>Only <c>N</c> formats print the row counts, so no magnitude prints an exponent. Culture follows
    /// <paramref name="provider"/>, the current culture by default.</item>
    /// </list>
    /// </summary>
    public static (string Actual, string Expected, string? Percent) PrintActualOfExpected(
        double actualRows, double expectedRows, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;

        var percent = expectedRows > 0 ? WholePercent(actualRows, expectedRows, provider) : null;

        var actualText = "";
        var expectedText = "";
        for (var decimals = 0; decimals <= MaxAgreementDecimals; decimals++)
        {
            actualText = PrintRows(actualRows, decimals, provider);
            expectedText = PrintRows(expectedRows, decimals, provider);

            if (percent == null || PrintedNumbersGive(percent, actualText, expectedText, provider))
                break;
        }

        return (actualText, expectedText, percent);
    }

    private static string WholePercent(double actualRows, double expectedRows, IFormatProvider provider)
        => (actualRows / expectedRows * 100).ToString("F0", provider);

    /// <summary>
    /// One row count as text: a whole number is always <c>N0</c>, any other number <c>N{decimals}</c>. A non-zero
    /// value that would print as zero is reprinted to its first significant digit instead.
    /// </summary>
    private static string PrintRows(double value, int decimals, IFormatProvider provider)
    {
        var text = value.ToString(Math.Floor(value) == value ? "N0" : NFormat(decimals), provider);

        if (value != 0 && double.IsFinite(value)
            && double.TryParse(text, NumberStyles.Number, provider, out var printed) && printed == 0)
        {
            var firstSignificantDecimals = -(int)Math.Floor(Math.Log10(Math.Abs(value)));
            text = value.ToString(NFormat(firstSignificantDecimals), provider);
        }

        return text;
    }

    /// <summary>True when the whole percent of the two printed numbers is the printed percentage.</summary>
    private static bool PrintedNumbersGive(string percent, string actualText, string expectedText, IFormatProvider provider)
    {
        if (!double.TryParse(actualText, NumberStyles.Number, provider, out var printedActual)
            || !double.TryParse(expectedText, NumberStyles.Number, provider, out var printedExpected))
        {
            return false;
        }

        // A printed divisor of zero gives NaN or Infinity, which never equals a percentage.
        var printedPercent = printedActual / printedExpected * 100;
        return double.IsFinite(printedPercent) && printedPercent.ToString("F0", provider) == percent;
    }

    private static string NFormat(int decimals) => string.Create(CultureInfo.InvariantCulture, $"N{decimals}");
}
