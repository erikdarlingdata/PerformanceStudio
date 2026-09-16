using System;
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
/// that quantity is), that a bar's length really is its share of the card's largest value, that a
/// metric with no activity says so instead of drawing empty tracks, and that the legend is drawn
/// once above the cards rather than once inside each.</para>
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
        RunWithOverview((control, _) =>
        {
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
        RunWithOverview((control, _) =>
        {
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
        RunWithOverview((control, _) =>
        {
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
            Assert.Equal(4, Tracks(cpu).Count);
        });
    }

    /// <summary>
    /// The track's Background is load-bearing twice over: it is the visible zero-to-full reference a
    /// bar is read against, and because Avalonia hit-tests what a control actually drew, it is also
    /// what makes the whole row answer the pointer. Delete that one setter from the styles and the
    /// bar tooltip and the drill-down menu both go dead on everything but the coloured fill —
    /// without a single other assertion in this file noticing.
    /// </summary>
    [Fact]
    public void TheTrackIsActuallyPaintedAndNotJustClassed()
    {
        RunWithOverview((control, _) =>
        {
            control.ApplyMetrics(SampleMetrics());

            Assert.All(Tracks(Card(control, metricIndex: 0)), track =>
            {
                Assert.NotNull(track.Background);
                Assert.NotNull(FillOf(track).Background);
            });
        });
    }

    [Fact]
    public void TheLegendIsDrawnOnceAboveTheCardsAndNotInsideThem()
    {
        RunWithOverview((control, _) =>
        {
            control.ApplyMetrics(SampleMetrics());

            var legend = control.FindControl<WrapPanel>("DatabaseLegend")!;
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
        RunWithOverview((control, window) =>
        {
            control.ApplyMetrics(
            [
                NewMetrics("full", cpu: 1000, executions: 1, writes: 0),
                NewMetrics("half", cpu: 500, executions: 1, writes: 0),
                NewMetrics("none", cpu: 0, executions: 1, writes: 0),
            ]);

            // The cards were rebuilt after the last layout pass, so they have not been arranged yet.
            window.UpdateLayout();

            var bars = Tracks(Card(control, metricIndex: 0))
                .Select(track => (Track: track.Bounds.Width, Fill: FillOf(track).Bounds.Width))
                .ToList();

            Assert.Equal(3, bars.Count);
            Assert.All(bars, b => Assert.True(b.Track > 20,
                $"the track measured {b.Track}px, which is too narrow for the shares below to mean anything"));

            AssertShareOfTrack(bars[0], 1.0);
            AssertShareOfTrack(bars[1], 0.5);
            Assert.Equal(0.0, bars[2].Fill);
        });
    }

    /// <summary>
    /// The width budget, at the width the app actually opens at, in the case that breaks it.
    ///
    /// <para>Seven cards share the row, and the name and value columns are both Auto, so they win
    /// their width against the star-sized track rather than yielding it. A long database name
    /// beside a twelve-digit read count starved the bar to about four pixels at MainWindow's
    /// default 1280 — the bar being, again, the entire point of the card. The name is capped and
    /// big counts step up a unit; this is what says both still hold.</para>
    /// </summary>
    [Fact]
    public void TheBarSurvivesALongNameBesideABigNumberAtTheDefaultWindowWidth()
    {
        RunWithOverview((control, window) =>
        {
            control.ApplyMetrics(
            [
                NewMetrics("StackOverflow2013", cpu: 41_152_263_004, executions: 9_000_000, writes: 1),
                NewMetrics("StackOverflow2010", cpu: 20_000_000_000, executions: 4_000_000, writes: 1),
            ]);
            window.UpdateLayout();

            // Card 3 is Total Reads, the widest number on the dashboard.
            var reads = Card(control, metricIndex: 3);
            Assert.Equal(["123.5B", "60B"], TextByClass(reads, "barValue"));

            /* The bar has the card's whole content width because the labels are on their own line
               above it. When they shared a line with it, this measured 14px — and 4px before the
               value label learned to step up a unit. */
            var track = Tracks(reads)[0].Bounds.Width;
            var value = reads.GetLogicalDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("barValue")).Bounds.Width;
            Assert.True(track >= 100,
                $"the bar track measured {track}px at a {window.Width}px window " +
                $"(card {reads.Bounds.Width}, value label {value}) — it was 14px when the labels " +
                "shared the bar's line, and 4px before that");
        },
        // MainWindow defaults to 1280 and the control sits inside tab chrome, so this is generous.
        windowWidth: 1280);
    }

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

    /// <summary>
    /// Others in Avg mode has to be re-averaged over the tail's executions, not added up out of the
    /// tail's averages. Adding rates together produces a number that is not a rate: the tail here
    /// averages 2.5ms, and summing its two averages would claim 10ms — four times too high, enough
    /// to outrank the real database on the card, become the scale every other bar is drawn against,
    /// and squash them all to slivers.
    /// </summary>
    [Fact]
    public void OthersInAvgModeIsTheTailsAverageAndNotTheSumOfItsAverages()
    {
        List<DatabaseMetrics> metrics =
        [
            NewMetrics("kept", cpu: 1000, executions: 10, writes: 0),   // averages 100
            NewMetrics("tail-slow", cpu: 700, executions: 300, writes: 0),
            NewMetrics("tail-fast", cpu: 300, executions: 100, writes: 0),
        ];

        var rows = QueryStoreOverviewControl.BuildBarRows(
            metrics, ["kept"], metricIndex: 0, showAverages: true);

        var others = rows.Single(r => r.IsOthers);
        Assert.Equal(2.5, others.Value, 6);          // (700 + 300) / (300 + 100)
        Assert.Equal(["kept", "Others"], rows.Select(r => r.Database).ToList());

        /* Executions has no per-execution average, so its Others row stays a plain sum in Avg mode
           — the one metric this re-averaging must not touch. */
        var executions = QueryStoreOverviewControl.BuildBarRows(
            metrics, ["kept"], metricIndex: 2, showAverages: true);

        Assert.Equal(400, executions.Single(r => r.IsOthers).Value);
    }

    [Fact]
    public void BigCountsStepUpAUnitInTheLabelAndKeepTheirDigitsInTheTooltip()
    {
        Assert.Equal("99,999", QueryStoreOverviewControl.FormatMetric(
            99_999, QueryStoreOverviewControl.MetricUnit.Count));
        Assert.Equal("500K", QueryStoreOverviewControl.FormatMetric(
            500_000, QueryStoreOverviewControl.MetricUnit.Count));
        Assert.Equal("1.2B", QueryStoreOverviewControl.FormatMetric(
            1_230_000_000, QueryStoreOverviewControl.MetricUnit.Count));

        Assert.Equal("123,456,789,012", QueryStoreOverviewControl.FormatMetricExact(
            123_456_789_012, QueryStoreOverviewControl.MetricUnit.Count));

        // Megabytes climb their own ladder rather than printing seven digits of megabyte.
        Assert.Equal("512 MB", QueryStoreOverviewControl.FormatMetric(
            512, QueryStoreOverviewControl.MetricUnit.Megabytes));
        Assert.Equal("2 GB", QueryStoreOverviewControl.FormatMetric(
            2048, QueryStoreOverviewControl.MetricUnit.Megabytes));
    }

    /// <summary>
    /// A session builds its overview once and keeps it, so the two on screen here are not two
    /// clicks in one session — they are two sessions, which is as close together as a session
    /// detached into its own window and the one it left. A process-wide RadioButton GroupName would
    /// let one pane's toggle clear the other pane's, leaving it with NEITHER segment checked while
    /// its cards still read Total. The hazard is unchanged by where the second one comes from,
    /// which is why the premise being out of date did not make the test wrong.
    /// </summary>
    [Fact]
    public void TwoOverviewsOnScreenDoNotShareOneToggle()
    {
        HeadlessUi.Run(() =>
        {
            var first = NewOverview();
            var second = NewOverview();

            var window = new Window
            {
                Content = new StackPanel { Children = { first, second } },
                Width = 1400,
                Height = 900
            };
            window.Show();
            window.UpdateLayout();

            try
            {
                first.ApplyMetrics(SampleMetrics());
                second.ApplyMetrics(SampleMetrics());

                first.FindControl<RadioButton>("AvgModeToggle")!.IsChecked = true;
                window.UpdateLayout();

                Assert.Equal("Avg CPU", CardHeaders(first)[0]);

                Assert.True(second.FindControl<RadioButton>("TotalModeToggle")!.IsChecked,
                    "the second overview's Total segment was cleared by the first overview's " +
                    "toggle, which leaves it showing Total with nothing latched");
                Assert.Equal("Total CPU", CardHeaders(second)[0]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheStateCardNamesEveryDatabaseAndWhatItsQueryStoreIsDoing()
    {
        RunWithOverview((control, _) =>
        {
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
        RunWithOverview((control, _) =>
        {
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
    /// One overview in one window, laid out so its styles are applied and its toggle segments are
    /// in a visual tree where they can group each other, and closed afterwards.
    ///
    /// <para>The close is not housekeeping. A window left open relies on HeadlessUi draining the
    /// dispatcher queue to keep the shared session alive — the #474 fix — and a test has no
    /// business leaning on another test's safety net.</para>
    /// </summary>
    private static void RunWithOverview(
        Action<QueryStoreOverviewControl, Window> body, double windowWidth = 1400)
    {
        HeadlessUi.Run(() =>
        {
            var control = NewOverview();
            var window = new Window { Content = control, Width = windowWidth, Height = 800 };
            window.Show();
            window.UpdateLayout();

            try
            {
                body(control, window);
            }
            finally
            {
                window.Close();
            }
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

    private static List<Border> Tracks(Border card) =>
        card.GetLogicalDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("barTrack"))
            .ToList();

    private static Border FillOf(Border track) =>
        track.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("barFill"));

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
