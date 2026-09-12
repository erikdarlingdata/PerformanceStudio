using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #507: the custom range popup used to close itself when a date was missing, and quietly move the
/// end to an hour after the start when the end landed first. Both read as the picker ignoring what
/// was typed, which is what the reporter concluded.
/// </summary>
public class CustomTimeRangeValidationTests
{
    private static readonly System.DateTime Noon = new(2026, 9, 4, 12, 0, 0, System.DateTimeKind.Utc);

    [Fact]
    public void AnOrderedRangeIsAccepted()
    {
        Assert.Null(TimeRangeSlicerControl.DescribeRangeProblem(Noon, Noon.AddHours(3)));
    }

    [Fact]
    public void AnEndBeforeTheStartIsRefusedInsteadOfMoved()
    {
        var problem = TimeRangeSlicerControl.DescribeRangeProblem(Noon, Noon.AddHours(-3));

        Assert.NotNull(problem);
        Assert.Contains("before the start", problem);
    }

    /// <summary>
    /// Equal bounds are their own message: "before the start" would be wrong, and the old code turned
    /// this into a silent one-hour range.
    /// </summary>
    [Fact]
    public void EqualStartAndEndSaysSoOnItsOwnTerms()
    {
        var problem = TimeRangeSlicerControl.DescribeRangeProblem(Noon, Noon);

        Assert.NotNull(problem);
        Assert.Contains("same", problem);
        Assert.DoesNotContain("before the start", problem);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AMissingDateIsReportedRatherThanClosingThePopup(bool hasStart, bool hasEnd)
    {
        var problem = TimeRangeSlicerControl.DescribeRangeProblem(
            hasStart ? Noon : null,
            hasEnd ? Noon.AddHours(1) : null);

        Assert.NotNull(problem);
        Assert.Contains("both", problem);
    }

    /// <summary>
    /// A minute apart is a legitimate range to enter. The slicer widens it to one hourly bucket when
    /// it applies, which is a property of Query Store's aggregation, not a reason to refuse the input.
    /// </summary>
    [Fact]
    public void AVeryShortRangeIsStillAValidEntry()
    {
        Assert.Null(TimeRangeSlicerControl.DescribeRangeProblem(Noon, Noon.AddMinutes(1)));
    }
}
