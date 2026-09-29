using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The Overview's host detaches — its own <c>DetachedFromVisualTree</c> cancels whatever load is
/// running — whenever the user switches to another top-level session tab: MainTabControl realises
/// only the selected tab's content, so a session sitting in the background has no visual root
/// (QuerySessionControl.axaml.cs's own detach handler says so outright). Switching a surface
/// segment within one session does not do this — <c>ApplySurface</c> only toggles <c>IsVisible</c>
/// — so it never cancelled anything and is not exercised here.
///
/// <para>Before this fix, coming back from that detach left the strip exactly as the cancellation
/// left it: nothing restarted the load, so a page switched away from mid-fetch stayed empty for
/// the rest of the session (E3). The fix remembers, at the moment of detach, whether work was
/// actually running — not just whether <c>_cts</c> is non-null, which stays true long after a load
/// has finished — and restarts the load on the next attach.</para>
///
/// <para>None of these pump the dispatcher between starting the load and switching away. A load
/// started from a synchronous test body is parked on its first dispatcher hop with a live token and
/// nothing sent, which is the state a real one is in for the whole of its network phases; a pump
/// would let it reach the socket.</para>
/// </summary>
public class OverviewReattachReloadTests
{
    [Fact]
    public void ReattachingRestartsALoadTheDetachCancelledMidFlight()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                var overview = StartAttachedLoad(window, session);

                var firstLoad = SessionHarness.OverviewLoadToken(overview);
                Assert.NotNull(firstLoad);
                Assert.False(firstLoad!.IsCancellationRequested);

                var firstTab = window.MainTabControl.SelectedItem;
                SwitchToAnotherTab(window);

                Assert.NotSame(firstTab, window.MainTabControl.SelectedItem);
                Assert.True(firstLoad.IsCancellationRequested,
                    "switching to another top-level tab did not detach the Overview's load");

                // Switch back. The attach handler saw work still running at the moment of detach
                // and should have started the load again on a token of its own.
                SwitchBackTo(window, firstTab);

                var secondLoad = SessionHarness.OverviewLoadToken(overview);
                Assert.NotNull(secondLoad);
                Assert.NotSame(firstLoad, secondLoad);
                Assert.False(secondLoad!.IsCancellationRequested);
                Assert.Same(secondLoad, SessionHarness.OverviewRunningToken(overview));
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A load that had already finished — or was cancelled some other way — before the detach ever
    /// happened leaves nothing for the next attach to restart. Its token is still sitting in
    /// <c>_cts</c>, which is why a detach cannot use that field to tell.
    /// </summary>
    [Fact]
    public void ReattachingDoesNotReloadAViewThatWasNotMidLoad()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                var overview = StartAttachedLoad(window, session);

                // Settles the load — cancelled, then drained until it has unwound and let go of
                // its claim on the "running" answer — before any detach ever happens.
                SessionHarness.StopOverviewLoad(overview);
                Assert.NotNull(SessionHarness.OverviewLoadToken(overview));
                Assert.Null(SessionHarness.OverviewRunningToken(overview));

                var firstTab = window.MainTabControl.SelectedItem;
                SwitchToAnotherTab(window);
                SwitchBackTo(window, firstTab);

                // The detach dropped the old token and nothing has asked for a new one.
                Assert.Null(SessionHarness.OverviewLoadToken(overview));
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Putting data on the time slicer raises its RangeChanged, whose handler cancels the load and
    /// runs the slowest phase (metrics and wait stats for every database) on a token of its own —
    /// while the cancelled load unwinds a dispatcher hop or two later. So on an ordinary load the
    /// answer to "is anything running?" has to survive the load's own exit: a switch away during
    /// that phase is the most likely one there is.
    /// </summary>
    [Fact]
    public void ReattachingRestartsARefreshAfterTheLoadThatStartedItHasUnwound()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                var overview = StartAttachedLoad(window, session);

                var load = SessionHarness.OverviewLoadToken(overview)!;
                var refresh = SessionHarness.PlantOverviewRefresh(overview);
                Assert.True(load.IsCancellationRequested);

                // The cancelled load reaches its exit. The refresh is still what is running.
                for (var pump = 0; pump < 10; pump++)
                    Dispatcher.UIThread.RunJobs();
                Assert.Same(refresh, SessionHarness.OverviewRunningToken(overview));

                var firstTab = window.MainTabControl.SelectedItem;
                SwitchToAnotherTab(window);
                Assert.True(refresh.IsCancellationRequested,
                    "the detach did not cancel the refresh that was running");

                SwitchBackTo(window, firstTab);

                var reload = SessionHarness.OverviewLoadToken(overview);
                Assert.NotNull(reload);
                Assert.NotSame(refresh, reload);
                Assert.Same(reload, SessionHarness.OverviewRunningToken(overview));
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Asks the session for its Overview, which starts a load, and lays the window out so the
    /// Overview is actually attached to the visual tree — its content is only realised by a layout
    /// pass, and a control that was never attached has no detach to cancel anything with.
    /// </summary>
    private static QueryStoreOverviewControl StartAttachedLoad(MainWindow window, QuerySessionControl session)
    {
        SessionHarness.OverviewSegment(session).IsChecked = true;
        var overview = SessionHarness.OverviewView(session)!;

        window.UpdateLayout();
        Assert.NotNull(TopLevel.GetTopLevel(overview));
        return overview;
    }

    private static void SwitchToAnotherTab(MainWindow window)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        window.UpdateLayout();
    }

    private static void SwitchBackTo(MainWindow window, object? tab)
    {
        window.MainTabControl.SelectedItem = tab;
        window.UpdateLayout();
    }
}
