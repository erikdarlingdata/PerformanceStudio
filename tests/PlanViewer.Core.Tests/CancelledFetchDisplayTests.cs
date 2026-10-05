using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Data.SqlClient;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Helpers;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #628: a cancelled Query Store fetch is a cancel, not an error.
///
/// <para>Microsoft.Data.SqlClient does not always report a cancel as an
/// <see cref="OperationCanceledException"/>. A token cancelled while the server is still running
/// the query comes back as a <c>SqlException</c> ("A severe error occurred on the current command.
/// ... Operation cancelled by user."), and every handler that decided by exception type alone
/// showed that as a failure — in the status strip, or on the Overview's "Last refresh failed"
/// badge. #626 fixed the three plan-capture paths by letting the run's token decide; these are the
/// Query Store paths and the session's strip.</para>
///
/// <para><b>How a cancel that is not an OperationCanceledException is made here without a
/// server.</b> A connection string SqlClient cannot parse makes <c>new SqlConnection(...)</c> throw an
/// <see cref="ArgumentException"/> before the token is looked at, which is exactly the shape of the
/// SqlException — a non-cancel exception arriving while the run's token is cancelled. The cancel
/// itself has to land after the run has made its token and before its query is sent, so the grid
/// and History tests cancel from the status text each fetch writes at that moment ("Loading time
/// slicer...", "Fetching plans...", ...): the write happens after the token exists and before the
/// connection is built, and a test cannot otherwise get a word in between two lines of one
/// synchronous method. The Overview's load parks on a dispatcher hop first, so there the test just
/// cancels the token it finds parked and lets the load continue (the same trick
/// OverviewReattachReloadTests uses to stop one).</para>
///
/// <para>Each cancelled case has an uncancelled twin: a failure under a token that was never
/// cancelled must still show its error exactly as before.</para>
/// </summary>
public class CancelledFetchDisplayTests
{
    /// <summary>
    /// Not a connection string. SqlConnection refuses it in its constructor, before any socket
    /// and before the token is read, so the failure it makes is instant, local, and independent of
    /// whatever SQL Server the machine running the suite has.
    /// </summary>
    private const string NotAConnectionString = "this is not a connection string";

    /// <summary>The message the failure above carries, which is what a handler would show.</summary>
    private static string FailureMessage()
    {
        try
        {
            using var _ = new SqlConnection(NotAConnectionString);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        throw new InvalidOperationException("SqlClient accepted a string that is not a connection string.");
    }

    // ---- the decision itself ------------------------------------------------------------

    [Fact]
    public void AnOperationCanceledExceptionIsACancelWhateverTheToken()
    {
        Assert.True(CancellationHelper.IsCancellation(new OperationCanceledException(), CancellationToken.None));
        Assert.True(CancellationHelper.IsCancellation(new TaskCanceledException(), CancellationToken.None));

        using var live = new CancellationTokenSource();
        Assert.True(CancellationHelper.IsCancellation(new OperationCanceledException(), live.Token));
    }

    /// <summary>
    /// The case the old handlers got wrong. A SqlException cannot be built outside SqlClient, and
    /// the decision never looks at which type it is, so any other exception stands in for it.
    /// </summary>
    [Fact]
    public void AnyExceptionIsACancelOnceTheRunsTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(CancellationHelper.IsCancellation(new InvalidOperationException("severe"), cts.Token));
        Assert.True(CancellationHelper.IsCancellation(new ArgumentException("severe"), cts.Token));
    }

    [Fact]
    public void AFailureUnderATokenThatWasNeverCancelledIsStillAFailure()
    {
        using var live = new CancellationTokenSource();

        Assert.False(CancellationHelper.IsCancellation(new InvalidOperationException("severe"), live.Token));
        Assert.False(CancellationHelper.IsCancellation(new InvalidOperationException("severe"), CancellationToken.None));
    }

    // ---- the session's status strip -----------------------------------------------------

    [Fact]
    public void TheStripSaysNothingAboutAFailureOfARunThatWasCancelled()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                session.SetStatusFromException(new InvalidOperationException(
                    "A severe error occurred on the current command. Operation cancelled by user."),
                    ct: cts.Token);
                session.SetStatusFromException(new InvalidOperationException("boom"), "Error: ", cts.Token);

                Assert.True(string.IsNullOrEmpty(Strip(session).Text),
                    $"a cancelled run's failure reached the strip as \"{Strip(session).Text}\"");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    [Fact]
    public void TheStripStillShowsAFailureOfARunThatWasNotCancelled()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                using var live = new CancellationTokenSource();

                session.SetStatusFromException(new InvalidOperationException("boom"), "Error: ", live.Token);
                Assert.Equal("Error: boom", Strip(session).Text);

                // No token at all is the callers that have no run to ask, and it behaves as before.
                session.SetStatusFromException(new InvalidOperationException("bang"));
                Assert.Equal("bang", Strip(session).Text);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    [Fact]
    public void TheStripStillSaysNothingAboutAnOperationCanceledExceptionWithNoToken()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                session.SetStatusFromException(new TaskCanceledException());

                Assert.True(string.IsNullOrEmpty(Strip(session).Text));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- the Overview, through the session that shows its failures ---------------------

    /// <summary>
    /// The load is cancelled while it is parked on its first dispatcher hop, then let go: the
    /// first thing it does is build a connection from a string SqlClient refuses. The failure that
    /// makes is not an OperationCanceledException and the load's token is cancelled — a user
    /// switching away from the Overview, or asking for it again — so the session must say nothing:
    /// not on its strip, and not on the control's own badge.
    /// </summary>
    [Fact]
    public void ACancelledOverviewLoadLeavesNeitherTheStripNorTheBadgeWithAnError()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var overview = StartOverviewLoad(session);
                SessionHarness.OverviewLoadToken(overview)!.Cancel();

                PumpUntilLoaded(session);

                Assert.True(string.IsNullOrEmpty(Strip(session).Text),
                    $"a cancelled load left \"{Strip(session).Text}\" on the strip");
                Assert.False(Badge(overview).IsVisible, "a cancelled load put up the refresh-failed badge");
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The twin: the same failure with the token left alone is a real one, and is shown the way it
    /// always was — on the strip, while the Overview is what the user is looking at.
    /// </summary>
    [Fact]
    public void AnOverviewLoadThatFailedWithoutBeingCancelledStillShowsItsError()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var overview = StartOverviewLoad(session);

                PumpUntilLoaded(session);

                Assert.Equal(FailureMessage(), Strip(session).Text);
                Assert.False(Badge(overview).IsVisible);
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The attach handler restarts a load a detach cancelled, and nothing awaits it but itself, so
    /// its failures go to the control's badge. Cancelled again, the restarted load is not one of
    /// them — and the badge is the display a cancel must never reach (#628).
    /// </summary>
    [Fact]
    public void ACancelledReloadAfterReattachDoesNotPutUpTheRefreshFailedBadge()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var overview = StartOverviewLoad(session);
                SwitchAwayAndBack(window);

                var reload = SessionHarness.OverviewLoadToken(overview)!;
                Assert.False(reload.IsCancellationRequested, "coming back did not restart the load");
                reload.Cancel();

                PumpUntilIdle(overview);

                Assert.False(Badge(overview).IsVisible, "a cancelled reload put up the refresh-failed badge");
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    [Fact]
    public void AReloadAfterReattachThatFailedWithoutBeingCancelledStillPutsUpTheBadge()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var overview = StartOverviewLoad(session);
                SwitchAwayAndBack(window);

                var reload = SessionHarness.OverviewLoadToken(overview)!;
                Assert.False(reload.IsCancellationRequested, "coming back did not restart the load");

                PumpUntilIdle(overview);

                Assert.True(Badge(overview).IsVisible);
                Assert.Contains(FailureMessage(), (string)ToolTip.GetTip(Badge(overview))!);
            }
            finally
            {
                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- the Query Store grid ------------------------------------------------------------

    /// <summary>
    /// Fetch pressed, then cancelled before its query goes out: "Cancelled.", the same as when
    /// SqlClient reports the cancel as an OperationCanceledException. This goes through the time
    /// slicer load, whose own handler used to turn a cancel into "Slicer: ..." and never let the
    /// caller see one.
    /// </summary>
    [Fact]
    public void ACancelledTimeSlicerLoadSaysCancelled()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                var cancelled = CancelWhenStatusSays(grid, "Loading time slicer...");

                Invoke(grid, "Fetch_Click", null, new RoutedEventArgs());

                Assert.True(cancelled(), "the fetch never announced itself, so nothing was cancelled");
                Assert.Equal("Cancelled.", StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    [Fact]
    public void ATimeSlicerLoadThatFailedWithoutBeingCancelledStillShowsItsError()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                Invoke(grid, "Fetch_Click", null, new RoutedEventArgs());

                Assert.Equal("Slicer: " + FailureMessage(), StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    /// <summary>
    /// The slicer load's contract with its caller: a cancel arrives as an OperationCanceledException,
    /// wrapping whatever SqlClient threw, and the strip is left alone for the caller to word.
    /// </summary>
    [Fact]
    public void TheTimeSlicerLoadHandsACancelBackAsAnOperationCanceledException()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var load = (Task)Invoke(grid, "LoadTimeSlicerDataAsync", "cpu", cts.Token, null, null)!;

                /* Asked for through the awaiter: an async method that throws an
                   OperationCanceledException ends up cancelled rather than faulted, and only the
                   awaiter hands the exception itself back. */
                Assert.True(load.IsCompleted);
                var cancel = Assert.ThrowsAny<OperationCanceledException>(() => load.GetAwaiter().GetResult());
                Assert.Equal(cts.Token, cancel.CancellationToken);
                Assert.IsAssignableFrom<ArgumentException>(cancel.InnerException);
                Assert.DoesNotContain("Slicer", StatusOf(grid).Text ?? "");
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    [Fact]
    public void ACancelledPlanFetchSaysCancelled()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                var cancelled = CancelWhenStatusSays(grid, "Fetching plans...");

                var fetch = (Task)Invoke(grid, "FetchPlansForRangeAsync")!;

                Assert.True(cancelled(), "the fetch never announced itself, so nothing was cancelled");
                Assert.True(fetch.IsCompletedSuccessfully);
                Assert.Equal("Cancelled.", StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    [Fact]
    public void APlanFetchThatFailedWithoutBeingCancelledStillShowsItsError()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                var fetch = (Task)Invoke(grid, "FetchPlansForRangeAsync")!;

                Assert.True(fetch.IsCompletedSuccessfully);
                Assert.Equal(FailureMessage(), StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    /// <summary>
    /// Changing the metric reloads the slicer. A reload that was cancelled — by the next change, or
    /// by the tab closing — says nothing: the strip keeps what it was saying, which is what an
    /// OperationCanceledException has always left there.
    /// </summary>
    [Fact]
    public void ACancelledMetricChangeLeavesTheStripAlone()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                PrepareMetricChange(grid);
                var cancelled = CancelWhenStatusSays(grid, "Refreshing metric...");

                Invoke(grid, "OrderBy_SelectionChanged", null, null);

                Assert.True(cancelled(), "the reload never announced itself, so nothing was cancelled");
                Assert.Equal("Refreshing metric...", StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    [Fact]
    public void AMetricChangeThatFailedWithoutBeingCancelledStillShowsItsError()
    {
        HeadlessUi.Run(() =>
        {
            var grid = NewGrid();
            try
            {
                PrepareMetricChange(grid);

                Invoke(grid, "OrderBy_SelectionChanged", null, null);

                Assert.Equal(FailureMessage(), StatusOf(grid).Text);
            }
            finally
            {
                grid.CancelFetch();
            }
        });
    }

    // ---- the History document ------------------------------------------------------------

    [Fact]
    public void ACancelledHistoryLoadSaysCancelled()
    {
        HeadlessUi.Run(() =>
        {
            var history = NewHistory();
            var status = history.FindControl<TextBlock>("StatusText")!;
            var cancelled = false;
            status.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty && (e.NewValue as string) == "Loading...")
                {
                    cancelled = true;
                    ((CancellationTokenSource)GetField(history, "_fetchCts")!).Cancel();
                }
            };

            var load = (Task)Invoke(history, "LoadHistoryAsync")!;

            Assert.True(cancelled, "the load never announced itself, so nothing was cancelled");
            Assert.True(load.IsCompletedSuccessfully);
            Assert.Equal("Cancelled.", status.Text);
        });
    }

    [Fact]
    public void AHistoryLoadThatFailedWithoutBeingCancelledStillShowsItsError()
    {
        HeadlessUi.Run(() =>
        {
            var history = NewHistory();

            var load = (Task)Invoke(history, "LoadHistoryAsync")!;

            Assert.True(load.IsCompletedSuccessfully);
            Assert.Equal(FailureMessage(), history.FindControl<TextBlock>("StatusText")!.Text);
        });
    }

    // ---- plumbing --------------------------------------------------------------------------

    private static TextBlock Strip(QuerySessionControl session) =>
        session.FindControl<TextBlock>("StatusText")!;

    private static TextBlock Badge(QueryStoreOverviewControl overview) =>
        overview.FindControl<TextBlock>("RefreshErrorBadge")!;

    private static TextBlock StatusOf(QueryStoreGridControl grid) =>
        grid.FindControl<TextBlock>("StatusText")!;

    /// <summary>
    /// Asks the session for its Overview, which starts a load that parks on its first dispatcher
    /// hop with a live token and nothing sent, and points that load at a connection string
    /// SqlClient will not build a connection from.
    /// </summary>
    private static QueryStoreOverviewControl StartOverviewLoad(QuerySessionControl session)
    {
        SessionHarness.PretendConnected(session);
        SessionHarness.OverviewSegment(session).IsChecked = true;

        var overview = SessionHarness.OverviewView(session)!;
        SetField(overview, "_masterConnectionString", NotAConnectionString);
        return overview;
    }

    /// <summary>
    /// Lets the load run to the end. It is done when the strip stops saying it is loading, which
    /// the session does by clearing it or by putting the failure there.
    /// </summary>
    private static void PumpUntilLoaded(QuerySessionControl session)
    {
        for (var pump = 0; pump < 100 && Strip(session).Text == "Loading Query Store Overview..."; pump++)
            Dispatcher.UIThread.RunJobs();

        Assert.NotEqual("Loading Query Store Overview...", Strip(session).Text);
    }

    /// <summary>
    /// Switches to another top-level tab and back, which detaches the Overview — cancelling the load
    /// it was running — and attaches it again, which restarts that load on a token of its own.
    /// </summary>
    private static void SwitchAwayAndBack(MainWindow window)
    {
        window.UpdateLayout();
        var firstTab = window.MainTabControl.SelectedItem;

        window.NewQuery_Click(window, new RoutedEventArgs());
        window.UpdateLayout();
        window.MainTabControl.SelectedItem = firstTab;
        window.UpdateLayout();
    }

    /// <summary>
    /// Lets the Overview's running load, if it has one, run to the end and its caller react to it.
    /// </summary>
    private static void PumpUntilIdle(QueryStoreOverviewControl overview)
    {
        for (var pump = 0; pump < 100 && SessionHarness.OverviewRunningToken(overview) != null; pump++)
            Dispatcher.UIThread.RunJobs();

        Assert.Null(SessionHarness.OverviewRunningToken(overview));

        for (var pump = 0; pump < 20; pump++)
            Dispatcher.UIThread.RunJobs();
    }

    private static QueryStoreGridControl NewGrid()
    {
        var grid = new QueryStoreGridControl(
            new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
            new NoCredentials(),
            new ServerUtcOffset(),
            initialDatabase: "master",
            databases: new List<string> { "master" });

        SetField(grid, "_connectionString", NotAConnectionString);
        return grid;
    }

    /// <summary>
    /// The shape a metric change needs before its handler will do anything: the initial load is
    /// over, and the box says something other than what was last fetched.
    /// </summary>
    private static void PrepareMetricChange(QueryStoreGridControl grid)
    {
        SetField(grid, "_initialOrderByLoaded", true);
        SetField(grid, "_lastFetchedOrderBy", "not-the-metric-on-screen");
    }

    private static QueryStoreHistoryControl NewHistory() =>
        new(connectionString: NotAConnectionString, queryHash: "0xABC", queryText: "select 1;",
            database: "master", serverOffset: new ServerUtcOffset());

    /// <summary>
    /// Cancels the grid's fetch the moment the status strip says <paramref name="text"/> — which
    /// each fetch does after it has made its token and before it builds a connection. Returns
    /// whether that happened, so a test whose fetch never got there cannot pass for the wrong reason.
    /// </summary>
    private static Func<bool> CancelWhenStatusSays(QueryStoreGridControl grid, string text)
    {
        var cancelled = false;
        StatusOf(grid).PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && (e.NewValue as string) == text)
            {
                cancelled = true;
                grid.CancelFetch();
            }
        };
        return () => cancelled;
    }

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(target, args);

    private static void SetField(object target, string name, object? value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static object? GetField(object target, string name) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);

    /// <summary>
    /// Windows-auth credentials so the constructors' connection-string builds ask for nothing; no
    /// test here ever opens a connection.
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
