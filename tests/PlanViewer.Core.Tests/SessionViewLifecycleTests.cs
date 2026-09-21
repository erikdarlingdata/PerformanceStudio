using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// What it costs to make the Overview a view rather than a document, and what the documents get in
/// exchange.
///
/// <para>The trade is deliberate in both directions. A view is built once and kept, so switching
/// away and back does not throw away what it fetched — and so a stale one has to be thrown away by
/// hand when the session connects somewhere else, which is the reconnect half below. A document is
/// realised only while it is the one selected, so leaving it detaches it, which is what lets a
/// History fetch notice it is no longer being waited for. Both halves would look fine in a
/// screenshot and neither would say a word if it stopped working.</para>
/// </summary>
public class SessionViewLifecycleTests
{
    /// <summary>
    /// Leaving a loading History document behind cancels its fetch, because leaving the documents
    /// detaches the one that was showing.
    /// </summary>
    /// <remarks>
    /// This was free while the documents were tabs of a TabControl: switching tabs unrealised the
    /// old one and the control heard about it. It has to keep being free now the surfaces are shown
    /// and hidden instead, and the only reason it is, is that a view never coexists with a selected
    /// document — so leaving one empties the host, which detaches whatever it was presenting.
    /// </remarks>
    [Fact]
    public void LeavingALoadingHistoryDocumentCancelsItsFetch()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var history = SessionHarness.NewHistory();
                session.AddHistorySubTab("History — 0xABC", history);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Assert.Same(history, SessionHarness.DocumentHost(session).Content);

                var fetch = SessionHarness.PlantHistoryFetch(history);
                Assert.False(fetch.IsCancellationRequested);

                SessionHarness.EditorSegment(session).IsChecked = true;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Assert.Null(SessionHarness.DocumentHost(session).Content);
                Assert.True(fetch.IsCancellationRequested,
                    "the History document left the screen and its fetch carried on waiting");

                // The document itself is still open; navigating away is not closing.
                Assert.Single(SessionHarness.Strip(session).Items);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Connecting somewhere else throws the Overview away, and asking for it again builds a new one
    /// rather than showing the old server's.
    /// </summary>
    /// <remarks>
    /// The control asks its server what it supports once, at construction, and holds that answer for
    /// good — so an Overview kept across a reconnect would be quietly wrong about the new server for
    /// the rest of the session. Rebuilding is also what the toolbar button always did, which was a
    /// fresh control per click.
    /// </remarks>
    [Fact]
    public void ConnectingSomewhereElseBuildsANewOverview()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                var first = SessionHarness.OverviewView(session);
                Assert.NotNull(first);
                Assert.Same(first, SessionHarness.OverviewHost(session).Content);
                SessionHarness.StopOverviewLoad(first);

                SessionHarness.PretendConnected(session, serverName: "tcp:127.0.0.1,2");
                SessionHarness.InvalidateOverview(session);

                Assert.Null(SessionHarness.OverviewView(session));
                Assert.Null(SessionHarness.OverviewHost(session).Content);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                var second = SessionHarness.OverviewView(session);

                Assert.NotNull(second);
                Assert.NotSame(first, second);
                Assert.Same(second, SessionHarness.OverviewHost(session).Content);
                SessionHarness.StopOverviewLoad(second);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Throwing the Overview away while it is the thing on screen lands the session on the editor,
    /// and throwing it away while it is not moves nobody.
    /// </summary>
    [Fact]
    public void InvalidatingTheOverviewOnlyMovesYouIfYouWereLookingAtIt()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                Assert.Equal(QuerySessionControl.SessionSurface.Overview, session.SelectedView);

                SessionHarness.InvalidateOverview(session);

                /* There is nothing to show and nothing to rebuild it from yet, so leaving the user
                   in front of it would leave them in front of a blank. */
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.True(SessionHarness.EditorSegment(session).IsChecked);
                Assert.False(SessionHarness.OverviewSegment(session).IsChecked);

                // Build it again, then go and look at a document instead.
                SessionHarness.OverviewSegment(session).IsChecked = true;
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);

                SessionHarness.InvalidateOverview(session);

                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
                Assert.Same(document, SessionHarness.Strip(session).SelectedItem);
                Assert.Null(SessionHarness.OverviewView(session));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Asking for the Overview again reloads the one you have rather than showing you what it
    /// fetched last time.
    /// </summary>
    /// <remarks>
    /// The control has no timer: an explicit ask is the only thing that ever refreshes it, so the
    /// alternative to reloading is coming back to a view that quietly shows yesterday's numbers. The
    /// second ask comes from the toolbar button, which is the way in that can be pressed while the
    /// view is already showing — and is therefore also the one that can re-enter a load in flight,
    /// which is why the newer of two loads cancels the older.
    /// </remarks>
    [Fact]
    public void AskingForTheOverviewAgainReloadsTheSameControl()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                var overview = SessionHarness.OverviewView(session)!;
                var firstLoad = SessionHarness.OverviewLoadToken(overview);
                Assert.NotNull(firstLoad);

                session.FindControl<Button>("QueryStoreOverviewButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Same(overview, SessionHarness.OverviewView(session));
                Assert.Same(overview, SessionHarness.OverviewHost(session).Content);

                var secondLoad = SessionHarness.OverviewLoadToken(overview);
                Assert.NotSame(firstLoad, secondLoad);
                Assert.True(firstLoad!.IsCancellationRequested,
                    "the second ask left the first load running alongside it");

                SessionHarness.StopOverviewLoad(overview);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A reload keeps the window the user dragged the slicer to, and keeps it a window rather than
    /// a point.
    /// </summary>
    /// <remarks>
    /// <para>Both halves are the cost of reloading on every ask. Throwing the chosen range away
    /// would mean coming back to the view silently resets what you were looking at — so the range is
    /// re-applied, clamped into whatever data the reload just fetched.</para>
    ///
    /// <para>And clamped is where the second half comes from: each end clamps on its own, so a range
    /// that has aged off the front of the data window maps both of its ends to the same place. A
    /// zero-width selection refreshes over an empty window with its two handles on top of each
    /// other. The floor is the same one the hand-entered range has always had.</para>
    ///
    /// <para><b>A remembered range can drift off either end, and both are answered here.</b> Off
    /// the FRONT — the case normal aging produces, because Query Store ages data out of the back
    /// of its history and the window's far edge moves forward — both ends clamp to 0 and the
    /// floor raises the end off the start. Past the END — which aging never produces but a
    /// server-side Query Store purge or reset between reselects does — both ends clamp to 1.0,
    /// raising the end has nowhere to go, and the floor pulls the START back instead.
    /// <see cref="RestoredWidth"/> is the shape both cases share: same call, data placed the
    /// other side of the kept range.</para>
    /// </remarks>
    [Fact]
    public void AReloadKeepsTheChosenRangeAndNeverCollapsesIt()
    {
        HeadlessUi.Run(() =>
        {
            var slicer = new TimeRangeSlicerControl();
            var window = new Window { Content = slicer, Width = 1000, Height = 300 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var data = Hours(epoch, 48);

                slicer.LoadData(data, "cpu");
                var byDefault = slicer.SelectionStart!.Value;

                // A range the user picked, well inside the data and nothing like the default.
                var chosenStart = epoch.AddHours(10);
                var chosenEnd = epoch.AddHours(20);
                slicer.LoadData(data, "cpu", chosenStart, chosenEnd);

                Assert.NotEqual(byDefault, slicer.SelectionStart!.Value);
                AssertWithinTheHour(chosenStart, slicer.SelectionStart!.Value);
                AssertWithinTheHour(chosenEnd, slicer.SelectionEnd!.Value);

                /* Leave the session open long enough and the remembered range falls off the front
                   of the window: Query Store keeps aging data out, and the reload fetches from
                   wherever it starts now. Both of the kept range's ends are before the first
                   bucket, so both clamp to the same place, and the floor is what keeps the
                   selection a range. */
                var droppedOffTheFront = RestoredWidth(
                    slicer, Hours(epoch.AddHours(100), 48), chosenStart, chosenEnd);

                Assert.True(droppedOffTheFront > TimeSpan.Zero,
                    $"a range off the front of the new window collapsed onto itself at " +
                    $"{slicer.SelectionStart}");

                /* The other edge: a purge or reset hands the reload a window that ends before the
                   remembered range begins. Both ends clamp to the slicer's far end, where pushing
                   the end out can do nothing — this asserts the floor pulls the start back. */
                var pushedPastTheEnd = RestoredWidth(
                    slicer, Hours(epoch.AddHours(-100), 48), chosenStart, chosenEnd);

                Assert.True(pushedPastTheEnd > TimeSpan.Zero,
                    $"a range past the end of the new window collapsed onto itself at " +
                    $"{slicer.SelectionEnd}");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>
    /// A session with the Overview open still has an empty editor behind it, and opening a view
    /// is not opening anything: the document strip stays empty. The get-started panel proved
    /// that half before #540; now the connection the Overview needs is itself enough to retire
    /// the panel, so the strip carries the claim and the overlay is pinned to staying down.
    /// </summary>
    [Fact]
    public void TheOverviewDoesNotCountAsHavingOpenedSomething()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                // The refresh the real connect block runs after flipping the toolbar (#540).
                SessionHarness.RefreshEmptyState(session);
                Assert.Empty(session.QueryEditor.Text);

                SessionHarness.OverviewSegment(session).IsChecked = true;
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));

                Assert.Empty(SessionHarness.Strip(session).Items);
                Assert.False(SessionHarness.EditorView(session).IsVisible,
                    "the overlay lives inside the editor view, which is hidden while a view shows");

                SessionHarness.EditorSegment(session).IsChecked = true;
                window.UpdateLayout();

                Assert.False(SessionHarness.EmptyStateOverlay(session).IsVisible,
                    "connected — coming back to the editor shows the editor, not get-started (#540)");
                Assert.True(SessionHarness.EditorView(session).IsVisible);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A History document still detaches into its own window and still comes back.
    /// </summary>
    /// <remarks>
    /// The step that makes this work looks like dead code now that the strip renders headers only:
    /// the tab's content is set to null before the control is handed to the new window. It is not
    /// dead. Nulling it is what releases the control from the session's host through the same chain
    /// that presents it, and two hosts holding one control is an exception rather than a cosmetic
    /// problem. On the path taken here it is belt and braces — removing the selected document
    /// releases it anyway — but the same line is the only guard on the path where the detached
    /// document was not the selected one.
    /// </remarks>
    [Fact]
    public void AHistoryDocumentStillDetachesAndRedocks()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            Window? detached = null;
            try
            {
                var history = SessionHarness.NewHistory();
                session.AddHistorySubTab("History — 0xABC", history);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                var document = SessionHarness.Documents(session).Single();
                DetachButton(document).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                detached = history.GetLogicalAncestors().OfType<Window>().Single();
                Assert.NotSame(window, detached);
                Assert.Equal("History — 0xABC", detached.Title);

                // Gone from the session, which had nothing else open, so it lands on the editor.
                Assert.Empty(SessionHarness.Strip(session).Items);
                Assert.Null(SessionHarness.DocumentHost(session).Content);
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);

                RedockButton(detached).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                detached = null;

                var redocked = SessionHarness.Documents(session).Single();
                Assert.Same(history, redocked.Content);
                Assert.Same(history, SessionHarness.DocumentHost(session).Content);
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
            }
            finally
            {
                detached?.Close();
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static List<QueryStoreTimeSlice> Hours(DateTime startUtc, int count) =>
        Enumerable.Range(0, count).Select(i => new QueryStoreTimeSlice
        {
            IntervalStartUtc = startUtc.AddHours(i),
            TotalCpu = 100,
            TotalDuration = 200,
            TotalExecutions = 10,
        }).ToList();

    /// <summary>
    /// Reloads the slicer with a fresh window of data while asking it to keep a range the user
    /// chose, and says how wide the restored selection came out.
    /// </summary>
    /// <remarks>
    /// Written as a helper rather than inline because a remembered range can drift off either end
    /// of the new data and the arrangement is identical for both: the only difference is whether
    /// <paramref name="data"/> is placed after the kept range or before it. A caller adding the
    /// other end asserts on the width this returns and needs nothing else. Zero is the failure —
    /// two handles on top of each other, refreshing over an empty window.
    /// </remarks>
    private static TimeSpan RestoredWidth(TimeRangeSlicerControl slicer,
        List<QueryStoreTimeSlice> data, DateTime keptStart, DateTime keptEnd)
    {
        slicer.LoadData(data, "cpu", keptStart, keptEnd);
        return slicer.SelectionEnd!.Value - slicer.SelectionStart!.Value;
    }

    /// <summary>
    /// The slicer stores its selection as a position within the data rather than a timestamp, so a
    /// range that goes in and comes out again lands on the nearest edge of the bucket it fell in.
    /// An hour is one bucket.
    /// </summary>
    private static void AssertWithinTheHour(DateTime expected, DateTime actual) =>
        Assert.True(Math.Abs((actual - expected).TotalMinutes) < 60,
            $"expected about {expected:u}, got {actual:u}");

    /// <summary>The ↗ on a History header, which sits between its label and its close button.</summary>
    private static Button DetachButton(TabItem document) =>
        ((StackPanel)document.Header!).Children.OfType<Button>().First();

    /// <summary>Same shape as DetachedUnsavedChangesTests.RedockButton: the wrapper's one button.</summary>
    private static Button RedockButton(Window detached) =>
        ((DockPanel)detached.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single();
}
