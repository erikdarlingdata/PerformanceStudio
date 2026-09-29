using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #611: the node row label printed both counts N0 but took its percentage from the unrounded
/// values, so a Key Lookup that ran 117 times for 1 row (estimate 0.00964372 each, 1.128 expected
/// in total) read "1 of 1 (89%)". PlanRowAccuracy adds the fewest decimals at which the printed
/// numbers give the printed percentage. The rule and these cases are PerformanceMonitor's (#4684),
/// so both apps print the same string for the same plan.
/// </summary>
public class PlanRowAccuracyTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ---- the rule ----------------------------------------------------------------------------

    /// <summary>Whole numbers print N0 and a missing or zero expectation drops the percentage, exactly as the label always did.</summary>
    [Theory]
    [InlineData(1.0, 1.0, "1 of 1 (100%)")]
    [InlineData(5.0, 4.0, "5 of 4 (125%)")]
    [InlineData(1234.5, 1000.0, "1,234 of 1,000 (123%)")] // N0 rounds an exact .5 to even
    [InlineData(105.5128, 103.694, "106 of 104 (102%)")]
    [InlineData(1234567.0, 2000000.0, "1,234,567 of 2,000,000 (62%)")]
    [InlineData(1.0, 0.0, "1 of 0")]
    [InlineData(0.0, 0.0, "0 of 0")]
    [InlineData(12.0, 0.0, "12 of 0")]
    [InlineData(0.0, 250.0, "0 of 250 (0%)")]
    [InlineData(1.0, 1000000.0, "1 of 1,000,000 (0%)")] // the percentage is whole; only the row counts are kept off zero
    public void WholeNumbers_PrintN0(double actual, double expected, string label)
    {
        Assert.Equal(label, PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant));
    }

    /// <summary>The fewest decimals at which the printed numbers give the printed percentage. A whole number keeps its N0
    /// text, and a fixed-point count means a trailing zero can appear ("0.60", "0.30").</summary>
    [Theory]
    [InlineData(1.0, 1.12831524, "1 of 1.128 (89%)")]
    [InlineData(0.0, 0.5, "0 of 0.5 (0%)")]
    [InlineData(0.5, 5.0, "0.5 of 5 (10%)")]
    [InlineData(3.0, 0.4, "3 of 0.4 (750%)")]
    [InlineData(0.1, 0.3, "0.1 of 0.3 (33%)")]
    [InlineData(2.5, 2.0, "2.5 of 2 (125%)")]
    [InlineData(100000.0, 12.5, "100,000 of 12.5 (800000%)")]
    [InlineData(0.6, 0.75, "0.60 of 0.75 (80%)")]
    [InlineData(0.25, 0.3, "0.25 of 0.30 (83%)")]
    [InlineData(0.99, 1.01, "0.99 of 1.01 (98%)")]
    [InlineData(0.6, 0.55555, "0.600 of 0.556 (108%)")]
    [InlineData(0.0104, 0.0096, "0.0104 of 0.0096 (108%)")]
    [InlineData(25.0, 16.4, "25 of 16.4 (152%)")]
    [InlineData(1.0, 0.4, "1 of 0.4 (250%)")]
    public void Decimals_AreAddedOnlyUntilTheNumbersAgreeWithThePercentage(double actual, double expected, string label)
    {
        Assert.Equal(label, PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant));
    }

    /// <summary>Four decimals is the ceiling for making the numbers agree, so a label can still contradict its
    /// percentage there. It still never prints a non-zero value as 0: 0.00001234 needs five decimals to show a digit, and
    /// the cap does not apply to that.</summary>
    [Theory]
    [InlineData(1.0, 0.00012345, "1 of 0.0001 (810045%)")]
    [InlineData(0.000123, 0.000456, "0.0001 of 0.0005 (27%)")]
    [InlineData(1.0, 0.00001234, "1 of 0.00001 (8103728%)")]
    [InlineData(0.0000004, 1.0, "0.0000004 of 1 (0%)")]
    [InlineData(0.00001, 0.0, "0.00001 of 0")]
    [InlineData(0.0, 0.000005, "0 of 0.000005 (0%)")]
    public void Cap_StopsAtFourDecimals_ButANonZeroValueNeverPrintsAsZero(double actual, double expected, string label)
    {
        Assert.Equal(label, PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant));
    }

    /// <summary>No magnitude prints an exponent.</summary>
    [Theory]
    [InlineData(1e15, 3e15, "1,000,000,000,000,000 of 3,000,000,000,000,000 (33%)")]
    [InlineData(1e21, 4e21, "1,000,000,000,000,000,000,000 of 4,000,000,000,000,000,000,000 (25%)")]
    public void LargeNumbers_PrintEveryDigit(double actual, double expected, string label)
    {
        Assert.Equal(label, PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant));
    }

    [Theory]
    [InlineData(1e300, 3e300)]
    [InlineData(1.0, double.MaxValue)]
    [InlineData(5e-324, 1e-320)]
    [InlineData(1e-300, 1e300)]
    public void ExtremeMagnitudes_NeverPrintAnExponent(double actual, double expected)
    {
        var label = PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant);
        Assert.DoesNotContain("E", label, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"^[\d,.]+ of [\d,.]+ \(\d+%\)$", RegexOptions.CultureInvariant), label);
    }

    /// <summary>The HTML export words the line its own way, so it takes the pieces: the same two numbers, and no
    /// percentage when there is nothing to divide by.</summary>
    [Fact]
    public void PrintActualOfExpected_ReturnsThePiecesTheLabelJoins()
    {
        Assert.Equal(("1", "1.128", "89"), PlanRowAccuracy.PrintActualOfExpected(1.0, 1.12831524, Invariant));
        Assert.Equal(("1", "0", (string?)null), PlanRowAccuracy.PrintActualOfExpected(1.0, 0.0, Invariant));
    }

    // ---- culture -----------------------------------------------------------------------------

    /// <summary>The numbers, the decimal mark and the group mark follow the caller's culture, and the agreement check
    /// parses the group mark back ("2.983" is 2983 in de-DE).</summary>
    [Theory]
    [InlineData(1.0, 1.12831524, "1 of 1,128 (89%)")]
    [InlineData(609.0, 2983.02, "609 of 2.983 (20%)")]
    [InlineData(0.6, 0.75, "0,60 of 0,75 (80%)")]
    public void Culture_FollowsTheCallers(double actual, double expected, string label)
    {
        Assert.Equal(label, PlanRowAccuracy.FormatActualOfExpected(actual, expected, new CultureInfo("de-DE")));
    }

    [Fact]
    public void Culture_DefaultsToTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1 of 1,128 (89%)", PlanRowAccuracy.FormatActualOfExpected(1.0, 1.12831524));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- a sweep -----------------------------------------------------------------------------

    /// <summary>2,000 pairs from 1e-6 up to 1e6, whole and fractional, a twentieth with no actual rows. Each label must
    /// (1) agree with its own percentage or have stopped at four decimals, (2) carry no exponent, and (3) never print a
    /// non-zero value as zero.</summary>
    [Fact]
    public void Sweep_EveryLabelAgreesOrHitTheCap_HasNoExponent_AndNeverPrintsANonZeroValueAsZero()
    {
        var random = new Random(4684);
        var shape = new Regex(@"^(?<a>\S+) of (?<e>\S+) \((?<p>\d+)%\)$", RegexOptions.CultureInvariant);
        int pairs = 0, withDecimals = 0, atTheCap = 0, pastTheCap = 0;

        for (var i = 0; i < 2000; i++)
        {
            var expected = Math.Pow(10, random.NextDouble() * 12 - 6);
            if (i % 3 == 0)
                expected = Math.Max(Math.Round(expected), 1);
            var actual = expected * Math.Pow(10, random.NextDouble() * 2 - 1);
            if (i % 5 == 0)
                actual = Math.Round(actual);
            if (i % 20 == 0)
                actual = 0;

            var label = PlanRowAccuracy.FormatActualOfExpected(actual, expected, Invariant);
            var context = $"actual {actual:R}, expected {expected:R} -> \"{label}\"";
            var match = shape.Match(label);
            Assert.True(match.Success, $"unexpected label shape: {context}");
            pairs++;

            // (2) no exponent
            Assert.False(label.Contains('E') || label.Contains('e'), $"exponent in {context}");

            var actualText = match.Groups["a"].Value;
            var expectedText = match.Groups["e"].Value;
            var printedActual = double.Parse(actualText, NumberStyles.Number, Invariant);
            var printedExpected = double.Parse(expectedText, NumberStyles.Number, Invariant);

            // (3) a non-zero value never prints as zero
            Assert.True(actual == 0 || printedActual != 0, $"actual printed as zero: {context}");
            Assert.True(printedExpected != 0, $"expected printed as zero: {context}");

            // (1) the printed numbers give the printed percentage, unless the search ran out at four decimals
            var decimals = Math.Max(DecimalPlaces(actualText), DecimalPlaces(expectedText));
            var agrees = (printedActual / printedExpected * 100).ToString("F0", Invariant) == match.Groups["p"].Value;
            Assert.True(agrees || decimals >= 4, $"the label contradicts its percentage before the cap: {context}");

            if (decimals > 0)
                withDecimals++;
            if (decimals >= 4)
                atTheCap++;
            if (decimals >= 5)
                pastTheCap++;
        }

        Assert.Equal(2000, pairs);
        // The sweep has to reach the interesting paths, or the three properties above prove little.
        Assert.True(withDecimals > 800, $"only {withDecimals} labels needed decimals");
        Assert.True(atTheCap > 200, $"only {atTheCap} labels reached the cap");
        Assert.True(pastTheCap > 100, $"only {pastTheCap} labels needed the first-significant-digit rule");
    }

    private static int DecimalPlaces(string number)
    {
        var point = number.IndexOf('.');
        return point < 0 ? 0 : number.Length - point - 1;
    }

    // ---- the surfaces ------------------------------------------------------------------------

    /// <summary>The issue's own example: key_lookup_plan's Key Lookup (NodeId 4, on the inner side of the Nested Loops)
    /// ran 117 times for 1 row. The App's node label reads "1 of 1.128 (89%)", not "1 of 1 (89%)".</summary>
    [Fact]
    public void KeyLookupPlan_AppNodeLabel_AgreesWithItsPercentage()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine("Plans", "key_lookup_plan.sqlplan");
            Assert.True(File.Exists(path), $"Test plan not found: {path}");
            var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var viewer = new PlanViewerControl();
            Assert.True(viewer.LoadPlan(xml, "key_lookup_plan.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
            var window = new Window { Content = viewer, Width = 1600, Height = 1000 };
            window.Show();
            window.UpdateLayout();

            var texts = viewer.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("1 of 1.128 (89%)", texts);
            Assert.DoesNotContain("1 of 1 (89%)", texts);
        });
    }

    /// <summary>The HTML export prints the same numbers, in its own "X of Y rows (P%)" wording.</summary>
    [Fact]
    public void KeyLookupPlan_HtmlExport_AgreesWithItsPercentage()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("key_lookup_plan.sqlplan");
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                PlanLayoutEngine.Layout(stmt);

        var result = ResultMapper.Map(plan, "key_lookup_plan.sqlplan");
        var html = HtmlExporter.Export(result, TextFormatter.Format(result));

        Assert.Contains("1 of 1.128 rows (89%)", html);
        Assert.DoesNotContain("1 of 1 rows (89%)", html);
    }

    /// <summary>The Web viewer is not referenced by this project, so this pins its node label to the shared formatter
    /// instead of rendering it.</summary>
    [Fact]
    public void WebNodeLabel_UsesTheSharedFormatter()
    {
        var razor = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "PlanViewer.Web", "Pages", "Index.razor"));

        Assert.Contains("PlanRowAccuracy.FormatActualOfExpected(node.ActualRows, RowEstimateHelper.GetExpectedRows(node))", razor);
        Assert.DoesNotContain("{expectedRows:N0}", razor);
    }

    private static string SolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PlanViewer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
