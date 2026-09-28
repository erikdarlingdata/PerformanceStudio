using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// E5: Server time display used to read one process-wide number, written by whichever connection
/// connected last. Two sessions on servers in different time zones, or one session that
/// reconnected, shifted each other's Query Store grid, slicer and History times by the wrong
/// server's offset. The offset now belongs to a connection (<see cref="ServerUtcOffset"/>), and
/// every document opened on that connection keeps the holder it got.
///
/// <para><b>Why the tests that show times run inside <see cref="HeadlessUi"/>.</b>
/// <see cref="TimeDisplayHelper.Current"/> is the user's display preference and stays global, so a
/// test that needs Server mode has to set it. Every UI body runs whole on the one UI thread, which
/// keeps another test from changing the mode halfway through, and <see cref="ServerMode"/> puts it
/// back. It is set AFTER a window is built, because <c>MainWindow</c>'s constructor applies the
/// saved preference. The conversion tests at the top take the mode as an argument and touch no
/// global at all.</para>
///
/// <para><b>What is not covered.</b> The offset query itself, and the History control copying its
/// holder onto the rows a fetch returns, both need a server. The rows are tested with the holder
/// already on them, and the History control is tested for which holder it was given.</para>
/// </summary>
public class ServerUtcOffsetPerConnectionTests
{
    private static readonly DateTime Noon = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Minutes from UTC, the way DATEDIFF(MINUTE, GETUTCDATE(), GETDATE()) reports them.
    private const int Berlin = 120;
    private const int NewYork = -300;

    private static readonly List<string> Databases = new() { "master", "Sales", "Ops" };

    // ---- The conversion takes the offset it is given ---------------------------------------------

    [Theory]
    [InlineData(Berlin)]
    [InlineData(NewYork)]
    [InlineData(330)] // a half-hour zone: the offset is minutes, not hours
    [InlineData(0)]
    public void ServerModeAddsTheOffsetItWasGiven(int minutes)
    {
        var shown = TimeDisplayHelper.ConvertForDisplay(Noon, TimeDisplayMode.Server, minutes);

        Assert.Equal(Noon.AddMinutes(minutes), shown);
    }

    [Theory]
    [InlineData(Berlin)]
    [InlineData(NewYork)]
    public void UtcModeShowsUtcWhateverOffsetItWasGiven(int minutes)
    {
        var shown = TimeDisplayHelper.ConvertForDisplay(Noon, TimeDisplayMode.Utc, minutes);

        Assert.Equal(Noon, shown);
        Assert.Equal(DateTimeKind.Utc, shown.Kind);
    }

    [Theory]
    [InlineData(Berlin)]
    [InlineData(NewYork)]
    public void LocalModeShowsThisMachinesTimeWhateverOffsetItWasGiven(int minutes)
    {
        var shown = TimeDisplayHelper.ConvertForDisplay(Noon, TimeDisplayMode.Local, minutes);

        Assert.Equal(Noon.ToLocalTime(), shown);
    }

    /// <summary>
    /// The overloads the controls call read the mode from the global and the offset from the
    /// argument. Nothing about the offset is global any more.
    /// </summary>
    [Fact]
    public void TheModeIsGlobalAndTheOffsetIsAnArgument()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();

            Assert.Equal(Noon.AddMinutes(Berlin), TimeDisplayHelper.ConvertForDisplay(Noon, Berlin));
            Assert.Equal(Noon.AddMinutes(NewYork), TimeDisplayHelper.ConvertForDisplay(Noon, NewYork));
            Assert.Equal("14:00", TimeDisplayHelper.FormatForDisplay(Noon, Berlin, "HH:mm"));
            Assert.Equal("07:00", TimeDisplayHelper.FormatForDisplay(Noon, NewYork, "HH:mm"));
        });
    }

    // ---- What shows a time reads its own connection's holder ------------------------------------

    [Fact]
    public void GridRowsOnTwoConnectionsEachShowTheirOwnServersTime()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();
            var plan = new QueryStorePlan { LastExecutedUtc = Noon };
            var berlin = new ServerUtcOffset { Minutes = Berlin };
            var newYork = new ServerUtcOffset { Minutes = NewYork };

            var leafOnBerlin = new QueryStoreRow(plan, berlin);
            var leafOnNewYork = new QueryStoreRow(plan, newYork);
            var groupOnNewYork = new QueryStoreRow(plan, 0, "0x1", new List<QueryStoreRow> { leafOnNewYork }, newYork);

            Assert.Equal("2026-09-28 14:00", leafOnBerlin.LastExecutedLocal);
            Assert.Equal("2026-09-28 07:00", leafOnNewYork.LastExecutedLocal);
            Assert.Equal("2026-09-28 07:00", groupOnNewYork.LastExecutedLocal);
        });
    }

    /// <summary>
    /// The offset arrives a moment after the connect, so a row can exist before it does. It reads
    /// the holder each time it formats, so it shows the real offset from then on.
    /// </summary>
    [Fact]
    public void ARowBuiltBeforeTheOffsetArrivesShowsItOnceItDoes()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();
            var holder = new ServerUtcOffset();
            var row = new QueryStoreRow(new QueryStorePlan { LastExecutedUtc = Noon }, holder);

            Assert.Equal("2026-09-28 12:00", row.LastExecutedLocal); // zero: reads as UTC

            holder.Minutes = Berlin;

            Assert.Equal("2026-09-28 14:00", row.LastExecutedLocal);
        });
    }

    [Fact]
    public void HistoryRowsOnTwoConnectionsEachShowTheirOwnServersTime()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();
            var onBerlin = HistoryRow(new ServerUtcOffset { Minutes = Berlin });
            var onNewYork = HistoryRow(new ServerUtcOffset { Minutes = NewYork });
            var unset = HistoryRow(null);

            Assert.Equal("2026-09-28 14:00", onBerlin.IntervalStartLocal);
            Assert.Equal("2026-09-28 14:00", onBerlin.LastExecutionLocal);
            Assert.Equal("2026-09-28 07:00", onNewYork.IntervalStartLocal);
            Assert.Equal("2026-09-28 07:00", onNewYork.LastExecutionLocal);

            // A row nobody gave a holder reads as UTC rather than borrowing someone else's offset.
            Assert.Equal("2026-09-28 12:00", unset.IntervalStartLocal);
            Assert.Equal("2026-09-28 12:00", unset.LastExecutionLocal);
        });
    }

    [Fact]
    public void SlicersOnTwoConnectionsEachLabelTheirRangeInTheirOwnServersTime()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();
            var onBerlin = new TimeRangeSlicerControl { ServerOffset = new ServerUtcOffset { Minutes = Berlin } };
            var onNewYork = new TimeRangeSlicerControl { ServerOffset = new ServerUtcOffset { Minutes = NewYork } };

            Assert.Contains("2026-01-01 02:00 → 2026-01-03 02:00", RangeLabelAfterLoading(onBerlin));
            Assert.Contains("2025-12-31 19:00 → 2026-01-02 19:00", RangeLabelAfterLoading(onNewYork));
        });
    }

    /// <summary>
    /// The custom-range popup turns what was typed back into UTC, and it has to subtract the
    /// slicer's own connection's offset: that was the one place the old static was read backwards.
    /// </summary>
    [Fact]
    public void TheCustomRangePopupConvertsBackWithItsOwnConnectionsOffset()
    {
        HeadlessUi.Run(() =>
        {
            using var _ = new ServerMode();
            var onBerlin = new TimeRangeSlicerControl { ServerOffset = new ServerUtcOffset { Minutes = Berlin } };
            var onNewYork = new TimeRangeSlicerControl { ServerOffset = new ServerUtcOffset { Minutes = NewYork } };
            var typed = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Unspecified);

            Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0), onBerlin.ConvertFromDisplay(typed));
            Assert.Equal(new DateTime(2026, 9, 28, 17, 0, 0), onNewYork.ConvertFromDisplay(typed));
        });
    }

    [Fact]
    public void RibbonsOnTwoConnectionsEachTipTheirBarsInTheirOwnServersTime()
    {
        HeadlessUi.Run(() =>
        {
            var onBerlin = new WaitStatsRibbonControl { ServerOffset = new ServerUtcOffset { Minutes = Berlin } };
            var onNewYork = new WaitStatsRibbonControl { ServerOffset = new ServerUtcOffset { Minutes = NewYork } };
            var berlinWindow = new Window { Content = onBerlin, Width = 600, Height = 200 };
            var newYorkWindow = new Window { Content = onNewYork, Width = 600, Height = 200 };
            berlinWindow.Show();
            newYorkWindow.Show();
            try
            {
                using var _ = new ServerMode();

                Assert.Contains("2026-01-01 02:00 – 03:00", FirstBarTip(onBerlin, berlinWindow));
                Assert.Contains("2025-12-31 19:00 – 20:00", FirstBarTip(onNewYork, newYorkWindow));
            }
            finally
            {
                berlinWindow.Close();
                newYorkWindow.Close();
            }
        });
    }

    // ---- A connection owns its holder ------------------------------------------------------------

    /// <summary>
    /// The finding's first failure: two sessions on servers in different time zones. Each session's
    /// documents carry the holder of the connection the session made, so neither can move the
    /// other's times.
    /// </summary>
    [Fact]
    public void TwoSessionsOnServersInDifferentTimeZonesKeepTheirOwnOffsets()
    {
        HeadlessUi.Run(() =>
        {
            var (windowA, sessionA) = SessionHarness.NewSession();
            var (windowB, sessionB) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(sessionA);
                SessionHarness.PretendConnected(sessionB);
                var berlin = sessionA.BeginServerConnection();
                berlin.Minutes = Berlin;
                var newYork = sessionB.BeginServerConnection();
                newYork.Minutes = NewYork;

                var gridA = sessionA.NewQueryStoreGrid("Sales", Databases, supportsWaitStats: false);
                var gridB = sessionB.NewQueryStoreGrid("Sales", Databases, supportsWaitStats: false);
                sessionA.AddQueryStoreDocument(gridA, "Sales");
                sessionB.AddQueryStoreDocument(gridB, "Sales");

                Assert.NotSame(berlin, newYork);
                Assert.Same(berlin, HolderOf(gridA));
                Assert.Same(newYork, HolderOf(gridB));

                // What each grid hands the controls it draws with.
                Assert.Same(berlin, gridA.FindControl<TimeRangeSlicerControl>("TimeRangeSlicer")!.ServerOffset);
                Assert.Same(berlin, gridA.FindControl<WaitStatsProfileControl>("WaitStatsProfile")!.ServerOffset);
                Assert.Same(newYork, gridB.FindControl<TimeRangeSlicerControl>("TimeRangeSlicer")!.ServerOffset);
                Assert.Same(newYork, gridB.FindControl<WaitStatsProfileControl>("WaitStatsProfile")!.ServerOffset);

                // Sessions connected after the fact do not reach back into the first one's documents.
                Assert.Equal(Berlin, berlin.Minutes);
                Assert.Equal(NewYork, newYork.Minutes);

                // And the two times a row on each of them would show, now that the mode is Server.
                using var _ = new ServerMode();
                var plan = new QueryStorePlan { LastExecutedUtc = Noon };
                Assert.Equal("2026-09-28 14:00", new QueryStoreRow(plan, HolderOf(gridA)).LastExecutedLocal);
                Assert.Equal("2026-09-28 07:00", new QueryStoreRow(plan, HolderOf(gridB)).LastExecutedLocal);
            }
            finally
            {
                ChromeTestCleanup.PutAway(windowA);
                ChromeTestCleanup.PutAway(windowB);
            }
        });
    }

    /// <summary>
    /// The finding's second failure: one session that reconnects. A document already open keeps
    /// the holder of the connection it was opened on, so the new server's offset cannot shift its
    /// times; a document opened after the reconnect gets the new connection's holder. The
    /// Overview is included because it is the one view the session rebuilds itself on a connect.
    /// </summary>
    [Fact]
    public void AfterAReconnectAnOpenDocumentKeepsItsOffsetAndANewOneGetsTheNewOne()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                var first = session.BeginServerConnection();
                first.Minutes = Berlin;

                SessionHarness.OverviewSegment(session).IsChecked = true;
                var overviewBefore = SessionHarness.OverviewView(session)!;
                SessionHarness.StopOverviewLoad(overviewBefore);

                var gridBefore = session.NewQueryStoreGrid("Sales", Databases, supportsWaitStats: false);
                session.AddQueryStoreDocument(gridBefore, "Sales");
                var tabBefore = SessionHarness.Documents(session).Last();

                // Reconnect somewhere else, the way the connect block does it.
                SessionHarness.PretendConnected(session, serverName: "tcp:127.0.0.1,2");
                var second = session.BeginServerConnection();
                second.Minutes = NewYork;
                SessionHarness.InvalidateOverview(session);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                var overviewAfter = SessionHarness.OverviewView(session)!;
                SessionHarness.StopOverviewLoad(overviewAfter);

                var gridAfter = session.NewQueryStoreGrid("Ops", Databases, supportsWaitStats: false);
                session.AddQueryStoreDocument(gridAfter, "Ops");

                Assert.NotSame(first, second);
                Assert.Equal(Berlin, first.Minutes); // the reconnect did not write to it

                // Already open: still the old server's.
                Assert.Same(first, HolderOf(gridBefore));
                Assert.Same(first, gridBefore.FindControl<TimeRangeSlicerControl>("TimeRangeSlicer")!.ServerOffset);
                Assert.Same(first, HolderOf(overviewBefore));

                // Opened after: the new server's.
                Assert.Same(second, HolderOf(gridAfter));
                Assert.Same(second, gridAfter.FindControl<TimeRangeSlicerControl>("TimeRangeSlicer")!.ServerOffset);
                Assert.Same(second, HolderOf(overviewAfter));
                Assert.NotSame(overviewBefore, overviewAfter);

                /* A History opened from the OLD grid after the reconnect reads the old grid's
                   server, so it gets the old holder, not the session's current one. */
                SessionHarness.PressHeader(session, tabBefore);
                window.UpdateLayout();
                var row = new QueryStoreRow(
                    new QueryStorePlan { QueryHash = "0xABC", QueryText = "select 1;", LastExecutedUtc = Noon },
                    first);
                ((ObservableCollection<QueryStoreRow>)FieldOf(gridBefore, "_filteredRows").GetValue(gridBefore)!).Add(row);
                gridBefore.FindControl<DataGrid>("ResultsGrid")!.SelectedItem = row;
                typeof(QueryStoreGridControl)
                    .GetMethod("ViewHistory_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(gridBefore, new object?[] { null, new RoutedEventArgs() });

                var history = Assert.IsType<QueryStoreHistoryControl>(SessionHarness.Documents(session).Last().Content);
                Assert.Same(first, HolderOf(history));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The connect block hands the holder to the fetch as an argument instead of letting the fetch
    /// read the session's field afterwards. The seam is what makes that safe: every call to it is
    /// a new holder, so an answer that lands late fills the connection that asked.
    /// </summary>
    [Fact]
    public void EveryConnectMakesANewHolder()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var first = session.BeginServerConnection();
                first.Minutes = Berlin;
                var second = session.BeginServerConnection();

                Assert.NotSame(first, second);
                Assert.Equal(0, second.Minutes);
                Assert.Equal(Berlin, first.Minutes);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>Sets Server mode for a test's body and puts the user's preference back after.</summary>
    private sealed class ServerMode : IDisposable
    {
        private readonly TimeDisplayMode _before = TimeDisplayHelper.Current;

        public ServerMode() => TimeDisplayHelper.Current = TimeDisplayMode.Server;

        public void Dispose() => TimeDisplayHelper.Current = _before;
    }

    private static QueryStoreHistoryRow HistoryRow(ServerUtcOffset? holder) => new()
    {
        IntervalStartUtc = Noon,
        LastExecutionUtc = Noon,
        ServerOffset = holder,
    };

    /// <summary>
    /// Loads two days of hourly data and selects all of it, so both ends of the range sit exactly
    /// on bucket edges and the label carries no rounding.
    /// </summary>
    private static string RangeLabelAfterLoading(TimeRangeSlicerControl slicer)
    {
        var data = Enumerable.Range(0, 48).Select(i => new QueryStoreTimeSlice
        {
            IntervalStartUtc = Epoch.AddHours(i),
            TotalCpu = 100,
            TotalDuration = 200,
            TotalExecutions = 10,
        }).ToList();

        slicer.LoadData(data, "cpu", Epoch, Epoch.AddHours(48));
        return slicer.FindControl<TextBlock>("RangeLabel")!.Text ?? "";
    }

    /// <summary>The tooltip text on the first bar a laid-out ribbon draws for one hour of data.</summary>
    private static string FirstBarTip(WaitStatsRibbonControl ribbon, Window window)
    {
        ribbon.SetData(new List<WaitCategoryTimeSlice>
        {
            new() { IntervalStartUtc = Epoch, WaitCategory = 1, WaitCategoryDesc = "CPU", WaitRatio = 1.0 },
        });
        window.UpdateLayout();
        ribbon.SetData(new List<WaitCategoryTimeSlice>
        {
            new() { IntervalStartUtc = Epoch, WaitCategory = 1, WaitCategoryDesc = "CPU", WaitRatio = 1.0 },
        });

        var canvas = ribbon.FindControl<Canvas>("RibbonCanvas")!;
        var bar = canvas.Children.OfType<Avalonia.Controls.Shapes.Rectangle>()
            .First(rectangle => ToolTip.GetTip(rectangle) is TextBlock);
        return ((TextBlock)ToolTip.GetTip(bar)!).Text ?? "";
    }

    private static ServerUtcOffset HolderOf(object control) =>
        (ServerUtcOffset)FieldOf(control, "_serverOffset").GetValue(control)!;

    private static FieldInfo FieldOf(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} no longer has a field called {name} - the test reaching " +
                "for it needs updating, not deleting.");
}
