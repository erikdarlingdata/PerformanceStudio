using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The Query Store overview dashboard used to draw fourteen cards — a Total row of seven metrics
/// and an Avg row of the same seven — as stacks of coloured pills with the database name cut to
/// twelve characters inside the pill and the value nowhere but a tooltip. It is one row of small
/// multiples now, behind a Total/Avg switch.
///
/// <para>What these pin is the part of that which is invisible in a screenshot and easy to get
/// subtly wrong: that the switch re-reads EVERY card rather than relabelling them, that the bars
/// in a card are ordered by the quantity they draw (and re-ordered when the switch changes what
/// that quantity is), that a metric with no activity says so instead of drawing empty tracks, and
/// that the legend is drawn once above the cards rather than once inside each.</para>
///
/// <para>The control is driven through ApplyStates/ApplyMetrics, the same seams its fetch path
/// uses. Manufacturing the data is the point: a test that reached a real Query Store would inherit
/// whatever that server happened to be doing, and none of this is about SQL.</para>
/// </summary>
public class QueryStoreOverviewCardsTests
{
    /// <summary>
    /// Four databases with a top-N of three, so there is always an Others row, and with totals and
    /// averages that rank DIFFERENTLY — by total CPU the order is alpha, bravo, charlie, Others;
    /// by average CPU it is alpha, Others, charlie, bravo. A switch that only swapped the headers
    /// would leave the first order in place and fail on the second.
    /// </summary>
    private static List<DatabaseMetrics> SampleMetrics(double writes = 500) =>
    [
        NewMetrics("alpha", cpu: 4000, executions: 4, writes: writes),
        NewMetrics("bravo", cpu: 3000, executions: 1000, writes: writes),
        NewMetrics("charlie", cpu: 2000, executions: 10, writes: writes),
        NewMetrics("delta", cpu: 1000, executions: 2, writes: writes),
    ];

    private static DatabaseMetrics NewMetrics(string name, double cpu, long executions, double writes) =>
        new()
        {
            DatabaseName = name,
            TotalCpu = cpu,
            TotalDuration = cpu * 2,
            TotalExecutions = executions,
            TotalReads = cpu * 3,
            TotalWrites = writes,
            TotalPhysicalReads = cpu / 2,
            TotalMemory = cpu / 4,
        };

    [Fact]
    public void TheTotalAvgSwitchRedrawsEveryCardRatherThanRelabellingIt()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);

            control.ApplyMetrics(SampleMetrics());

            Assert.Equal(
                ["Total CPU", "Total Duration", "Executions", "Total Reads", "Total Writes",
                 "Total Physical Reads", "Total Memory"],
                CardHeaders(control));

            Assert.Equal(["alpha", "bravo", "charlie", "Others"], BarNames(control, metricIndex: 0));
            Assert.Equal(["4.0s", "3.0s", "2.0s", "1.0s"], BarValues(control, metricIndex: 0));

            control.FindControl<RadioButton>("AvgModeToggle")!.IsChecked = true;

            /* Executions keeps its name in both modes: a count of executions has no per-execution
               average, and "Avg Executions" over the identical number was part of what made the
               duplicated row look like it carried twice the information. */
            Assert.Equal(
                ["Avg CPU", "Avg Duration", "Executions", "Avg Reads", "Avg Writes",
                 "Avg Physical Reads", "Avg Memory"],
                CardHeaders(control));

            // Re-sorted, not just relabelled: bravo ran a thousand times and drops to the bottom.
            Assert.Equal(["alpha", "Others", "charlie", "bravo"], BarNames(control, metricIndex: 0));
            Assert.Equal(["1.0s", "500ms", "200ms", "3ms"], BarValues(control, metricIndex: 0));
        });
    }

    /// <summary>
    /// Executions is the card the switch leaves alone, so it is the one that proves the redraw is
    /// driven by the data rather than by the mode: same name, same numbers, both ways.
    /// </summary>
    [Fact]
    public void TheExecutionsCardReadsTheSameInBothModes()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);
            control.ApplyMetrics(SampleMetrics());

            var totalNames = BarNames(control, metricIndex: 2);
            var totalValues = BarValues(control, metricIndex: 2);
            Assert.Equal(["1,000", "10", "4", "2"], totalValues);

            control.FindControl<RadioButton>("AvgModeToggle")!.IsChecked = true;

            Assert.Equal(totalNames, BarNames(control, metricIndex: 2));
            Assert.Equal(totalValues, BarValues(control, metricIndex: 2));
        });
    }

    [Fact]
    public void ACardWithNoActivityGoesQuietInsteadOfDrawingEmptyTracks()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);

            // Every database wrote nothing, so Total Writes (card 4) has nothing to draw.
            control.ApplyMetrics(SampleMetrics(writes: 0));

            var writes = Card(control, metricIndex: 4);
            Assert.Contains("empty", Header(writes).Classes);
            Assert.Equal(["No activity in range"], TextByClass(writes, "cardEmpty"));
            Assert.DoesNotContain(writes.GetLogicalDescendants().OfType<Border>(),
                b => b.Classes.Contains("barTrack"));

            // And the card next to it, which did have activity, is untouched by that.
            var cpu = Card(control, metricIndex: 0);
            Assert.DoesNotContain("empty", Header(cpu).Classes);
            Assert.Empty(TextByClass(cpu, "cardEmpty"));
            Assert.Equal(4, cpu.GetLogicalDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("barTrack")));
        });
    }

    [Fact]
    public void TheLegendIsDrawnOnceAboveTheCardsAndNotInsideThem()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);
            control.ApplyMetrics(SampleMetrics());

            var legend = control.FindControl<StackPanel>("DatabaseLegend")!;
            Assert.Equal(
                ["alpha", "bravo", "charlie", "Others"],
                legend.GetLogicalDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("legendLabel"))
                    .Select(t => t.Text).ToList());

            Assert.DoesNotContain(MetricsGrid(control).GetLogicalDescendants().OfType<Border>(),
                b => b.Classes.Contains("legendSwatch"));
        });
    }

    /// <summary>
    /// The defect this whole card was rebuilt for, measured after a real layout pass.
    ///
    /// <para>The pills that used to be here were proportioned against the CARD and drawn with
    /// nothing behind them, so there was no visible zero and no visible full scale and a bar could
    /// not be read at all: half the card's width meant "half of the largest" or "half of nothing"
    /// with no way to tell. What has to be true now is that one card is one linear scale anchored
    /// at zero — the longest bar fills its track, half the value fills half of it, and no value
    /// fills none of it.</para>
    /// </summary>
    [Fact]
    public void BarLengthIsThatBarsShareOfTheCardsLargestValue()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            var window = Show(control);

            control.ApplyMetrics(
            [
                NewMetrics("full", cpu: 1000, executions: 1, writes: 0),
                NewMetrics("half", cpu: 500, executions: 1, writes: 0),
                NewMetrics("none", cpu: 0, executions: 1, writes: 0),
            ]);

            // The cards were rebuilt after the last layout pass, so they have not been arranged yet.
            window.UpdateLayout();

            var fractions = Card(control, metricIndex: 0)
                .GetLogicalDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("barTrack"))
                .Select(track => (Track: track.Bounds.Width, Fill: FillOf(track).Bounds.Width))
                .ToList();

            Assert.Equal(3, fractions.Count);
            Assert.All(fractions, f => Assert.True(f.Track > 20,
                $"the track measured {f.Track}px, which is too narrow for the shares below to mean anything"));

            AssertShareOfTrack(fractions[0], 1.0);
            AssertShareOfTrack(fractions[1], 0.5);
            Assert.Equal(0.0, fractions[2].Fill);
        });
    }

    /// <summary>
    /// Within a pixel, because the two star columns that proportion a bar are laid out on whole
    /// pixels: half of a 75px track is 37.5px and is arranged at 38, which is 50.7% and is correct.
    /// A tolerance in decimal places of the RATIO would make this test a function of how wide the
    /// window in the harness happens to be.
    /// </summary>
    private static void AssertShareOfTrack((double Track, double Fill) bar, double share)
    {
        var expected = bar.Track * share;
        Assert.True(Math.Abs(bar.Fill - expected) <= 1.0,
            $"a bar that should have covered {share:P0} of its {bar.Track}px track — {expected}px — " +
            $"measured {bar.Fill}px");
    }

    private static Border FillOf(Border track) =>
        track.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("barFill"));

    /// <summary>
    /// The ordering rule on its own, including the two things the rendering tests cannot show
    /// cheaply: that Others sorts on its value like any other row rather than being pinned to the
    /// bottom, and that a tie breaks on the name so a redraw of unchanged data cannot reshuffle a
    /// card under the reader.
    /// </summary>
    [Fact]
    public void OthersIsOrderedByItsValueAndTiesBreakOnTheName()
    {
        List<DatabaseMetrics> metrics =
        [
            NewMetrics("kept", cpu: 10, executions: 1, writes: 0),
            NewMetrics("dropped-one", cpu: 40, executions: 1, writes: 0),
            NewMetrics("dropped-two", cpu: 60, executions: 1, writes: 0),
        ];

        var rows = QueryStoreOverviewControl.BuildBarRows(
            metrics, ["kept"], metricIndex: 0, showAverages: false);

        Assert.Equal(["Others", "kept"], rows.Select(r => r.Database).ToList());
        Assert.Equal(100, rows[0].Value);
        Assert.True(rows[0].IsOthers);

        var tied = QueryStoreOverviewControl.BuildBarRows(
            [NewMetrics("zulu", cpu: 5, executions: 1, writes: 0),
             NewMetrics("alpha", cpu: 5, executions: 1, writes: 0)],
            ["zulu", "alpha"], metricIndex: 0, showAverages: false);

        Assert.Equal(["alpha", "zulu"], tied.Select(r => r.Database).ToList());
    }

    [Fact]
    public void TheStateCardNamesEveryDatabaseAndWhatItsQueryStoreIsDoing()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);

            control.ApplyStates(
            [
                NewState("zulu", QueryStoreState.ReadWrite),
                NewState("alpha", QueryStoreState.ReadWrite),
                NewState("paused", QueryStoreState.ReadOnly),
                NewState("broken", QueryStoreState.Off),
            ]);

            var headline = control.FindControl<TextBlock>("StatesHeadline")!;
            Assert.Equal("Query Store healthy on 2 of 4 databases", headline.Text);

            /* Something is OFF, so the headline takes the worst state below it — the card answers
               "is anything wrong" before the reader gets to the list. */
            Assert.Contains("bad", headline.Classes);
            Assert.DoesNotContain("healthy", headline.Classes);

            var list = control.FindControl<StackPanel>("StatesList")!;

            // Broken first, then what stopped collecting, then the healthy ones alphabetically.
            Assert.Equal(
                ["broken", "paused", "alpha", "zulu"],
                TextByClass(list, "stateName"));
            Assert.Equal(
                ["off", "read only", "read write", "read write"],
                TextByClass(list, "stateValue"));

            var states = list.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("stateValue")).ToList();
            Assert.Contains("bad", states[0].Classes);
            Assert.Contains("warn", states[1].Classes);
            Assert.Contains("healthy", states[2].Classes);
        });
    }

    /// <summary>
    /// A redraw must not leave the previous verdict's colour behind it — the classes are Set, not
    /// Added, and this is what says so.
    /// </summary>
    [Fact]
    public void TheHeadlineDropsItsPreviousVerdictWhenTheStatesChange()
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            Show(control);

            control.ApplyStates([NewState("broken", QueryStoreState.Off)]);
            var headline = control.FindControl<TextBlock>("StatesHeadline")!;
            Assert.Contains("bad", headline.Classes);

            control.ApplyStates([NewState("fixed", QueryStoreState.ReadWrite)]);
            Assert.Equal("Query Store healthy on 1 of 1 database", headline.Text);
            Assert.Contains("healthy", headline.Classes);
            Assert.DoesNotContain("bad", headline.Classes);
            Assert.DoesNotContain("warn", headline.Classes);
        });
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static DatabaseQueryStoreState NewState(string name, QueryStoreState state) =>
        new() { DatabaseName = name, State = state };

    /// <summary>
    /// A top-N of three is passed rather than taken from settings, so the Others row exists no
    /// matter what the machine running this has configured.
    /// </summary>
    private static QueryStoreOverviewControl NewOverview() =>
        new(new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
            new NoCredentials(), topN: 3);

    /// <summary>
    /// Puts the control in a window and lays it out, so its styles are applied and the two toggle
    /// segments are in a visual tree where they can group each other.
    /// </summary>
    private static Window Show(Control control)
    {
        var window = new Window { Content = control, Width = 1400, Height = 800 };
        window.Show();
        window.UpdateLayout();

        return window;
    }

    private static Grid MetricsGrid(QueryStoreOverviewControl control) =>
        control.FindControl<Grid>("MetricsGrid")!;

    private static Border Card(QueryStoreOverviewControl control, int metricIndex) =>
        MetricsGrid(control).Children.OfType<Border>().ElementAt(metricIndex);

    private static TextBlock Header(Border card) =>
        card.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("cardHeader"));

    private static List<string> CardHeaders(QueryStoreOverviewControl control) =>
        MetricsGrid(control).Children.OfType<Border>().Select(c => Header(c).Text ?? "").ToList();

    private static List<string> BarNames(QueryStoreOverviewControl control, int metricIndex) =>
        TextByClass(Card(control, metricIndex), "barName");

    private static List<string> BarValues(QueryStoreOverviewControl control, int metricIndex) =>
        TextByClass(Card(control, metricIndex), "barValue");

    private static List<string> TextByClass(Control root, string className) =>
        root.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains(className))
            .Select(t => t.Text ?? "")
            .ToList();

    /// <summary>
    /// Windows-auth credentials so the constructor's connection-string build asks for nothing; no
    /// test here ever opens the connection.
    /// </summary>
    private sealed class NoCredentials : ICredentialService
    {
        public bool SaveCredential(string serverId, string username, string password) => false;
        public (string Username, string Password)? GetCredential(string serverId) => null;
        public bool DeleteCredential(string serverId) => false;
        public bool CredentialExists(string serverId) => false;
        public bool UpdateCredential(string serverId, string username, string password) => false;
    }
}
