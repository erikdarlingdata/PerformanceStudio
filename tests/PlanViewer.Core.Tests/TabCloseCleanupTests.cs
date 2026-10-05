using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Helpers;
using PlanViewer.App.Mcp;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using Xunit;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Closing a tab has to let go of what the tab was holding: the plan it registered with the MCP
/// session manager, and the work it had running against a server.
///
/// <para><b>Why the assertions name one session id.</b> <see cref="PlanSessionManager.Instance"/>
/// is process-wide and other tests register plans into it, so no test here counts sessions. Each
/// one reads the id of the viewer it opened and asks the manager about that id alone.</para>
///
/// <para><b>Why the tabs are closed through their own ✕.</b> The close paths are the code under
/// test. Removing a tab from its collection by hand would skip exactly the part that was missing.</para>
/// </summary>
public class TabCloseCleanupTests
{
    // ---- E2: the plan comes off the MCP session list ------------------------------------------

    /// <summary>
    /// A plan opened from a file lives in a window-level tab, and closing that tab used to remove
    /// the tab and leave the plan registered until the app exited.
    /// </summary>
    [Fact]
    public void ClosingAPlanTabTakesItsPlanOffTheMcpSessionList()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                window.LoadPlanFile(PlanPath());
                var tab = PlanTab(window);
                var id = McpSessionId(PlanViewerOf(tab));
                Assert.NotNull(PlanSessionManager.Instance.GetSession(id));

                SessionHarness.CloseFromHeader(tab);
                Dispatcher.UIThread.RunJobs();

                Assert.DoesNotContain(tab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.Null(PlanSessionManager.Instance.GetSession(id));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Closing a query session closes every plan in it. Its sub-tab viewers were never told, so
    /// each one stayed registered.
    /// </summary>
    [Fact]
    public void ClosingAQuerySessionTakesEveryPlanInItOffTheMcpSessionList()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var ids = SessionHarness.OpenPlanDocuments(session, 2)
                    .Select(document => McpSessionId((PlanViewerControl)document.Content!))
                    .ToList();
                Assert.Equal(2, ids.Distinct().Count());
                Assert.All(ids, id => Assert.NotNull(PlanSessionManager.Instance.GetSession(id)));

                SessionHarness.CloseFromHeader(TabOf(window, session));
                Dispatcher.UIThread.RunJobs();

                Assert.All(ids, id => Assert.Null(PlanSessionManager.Instance.GetSession(id)));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Every close door of a session's own strip funnels into one release. Pinned because it was
    /// the one place that already worked, and a fix for the doors around it must not break it.
    /// </summary>
    [Fact]
    public void ClosingOnePlanDocumentInASessionTakesOnlyThatPlanOffTheList()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 2);
                var closedId = McpSessionId((PlanViewerControl)documents[0].Content!);
                var keptId = McpSessionId((PlanViewerControl)documents[1].Content!);

                SessionHarness.CloseFromHeader(documents[0]);

                Assert.Null(PlanSessionManager.Instance.GetSession(closedId));
                Assert.NotNull(PlanSessionManager.Instance.GetSession(keptId));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A tab that started as a spinner and had a plan swapped into it is released as the plan
    /// viewer it became, because the release reads the content at close time.
    /// </summary>
    [Fact]
    public void ClosingATabWhosePlanReplacedASpinnerTakesThePlanOffTheList()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);

                var spinnerTab = Assert.Single(SessionHarness.Documents(session));
                Assert.IsNotType<PlanViewerControl>(spinnerTab.Content);
                session.ShowCapturedPlan(spinnerTab, SessionHarness.SamplePlanXml(), "Plan 1", "select 1;");

                var id = McpSessionId((PlanViewerControl)spinnerTab.Content!);
                Assert.NotNull(PlanSessionManager.Instance.GetSession(id));

                SessionHarness.CloseFromHeader(spinnerTab);

                Assert.Null(PlanSessionManager.Instance.GetSession(id));
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A plan detached into its own window is closed for good when that window closes. The tab that
    /// held it is gone by then, so the window's own close has to do the release.
    /// </summary>
    [Fact]
    public void ClosingADetachedPlanWindowTakesItsPlanOffTheMcpSessionList()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                window.LoadPlanFile(PlanPath());
                var tab = PlanTab(window);
                var id = McpSessionId(PlanViewerOf(tab));

                var detached = window.DetachTabToWindow(tab)!;
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(PlanSessionManager.Instance.GetSession(id));

                detached.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Null(PlanSessionManager.Instance.GetSession(id));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Detaching and re-docking move a plan and do not close it, so the plan stays listed the whole
    /// way through and only comes off when the tab it ends up in is closed.
    /// </summary>
    [Fact]
    public void DetachingAndReDockingAPlanKeepsItOnTheListUntilItIsClosed()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                window.LoadPlanFile(PlanPath());
                var viewer = PlanViewerOf(PlanTab(window));
                var id = McpSessionId(viewer);

                var detached = window.DetachTabToWindow(PlanTab(window))!;
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(PlanSessionManager.Instance.GetSession(id));

                RedockButton(detached).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                var redockedTab = PlanTab(window);
                Assert.Same(viewer, PlanViewerOf(redockedTab));
                Assert.NotNull(PlanSessionManager.Instance.GetSession(id));

                SessionHarness.CloseFromHeader(redockedTab);
                Dispatcher.UIThread.RunJobs();

                Assert.Null(PlanSessionManager.Instance.GetSession(id));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- E4: closing a tab cancels the work the tab owns --------------------------------------

    /// <summary>
    /// A capture or a query runs with no timeout, so a session closed while one is running used to
    /// leave it running on the server until it finished by itself.
    /// </summary>
    [Fact]
    public void ClosingAQuerySessionCancelsItsRunningCapture()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                // A session with typed text is asking to be saved before it closes. This test is
                // about what happens once the close goes ahead, so it has nothing unsaved.
                session.MarkClean();
                StartCapture(session);

                var run = CurrentRun(session);
                Assert.False(run.IsCancellationRequested);

                SessionHarness.CloseFromHeader(TabOf(window, session));
                Dispatcher.UIThread.RunJobs();

                Assert.True(run.IsCancellationRequested,
                    "the session was closed and its capture carried on against the server");
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The spinner tab is the run's only visible handle. Closing it is the user saying stop.
    /// </summary>
    [Fact]
    public void ClosingALoadingTabCancelsTheRunItIsShowing()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);

                var loadingTab = Assert.Single(SessionHarness.Documents(session));
                var run = CurrentRun(session);
                Assert.False(run.IsCancellationRequested);

                SessionHarness.CloseFromHeader(loadingTab);

                Assert.True(run.IsCancellationRequested,
                    "the loading tab was closed and the query behind it carried on");
                Assert.Empty(SessionHarness.Documents(session));

                // The cancelled run then unwinds and removes its tab a second time. That has to be
                // a no-op, not a second trip through the rules for where the user lands.
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(SessionHarness.Documents(session));
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A Query Store grid fetches in the background. Its tab used to close without telling it.
    /// </summary>
    [Fact]
    public void ClosingAQueryStoreGridDocumentCancelsItsFetchAndItsDatabaseCheck()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var grid = NewGrid();
                session.AddQueryStoreDocument(grid, "master");
                var document = Assert.Single(SessionHarness.Documents(session));

                // Stand-ins for a fetch and a picker check still waiting on a server.
                var fetch = new CancellationTokenSource();
                var check = new CancellationTokenSource();
                SetField(grid, "_fetchCts", fetch);
                SetField(grid, "_databaseCheckCts", check);

                SessionHarness.CloseFromHeader(document);

                Assert.True(fetch.IsCancellationRequested, "the grid's tab closed and its fetch kept going");
                Assert.True(check.IsCancellationRequested, "the grid's tab closed and its database check kept going");

                // Cancelled, never disposed: the fetch reads its token again when it wakes, and
                // Token on a disposed source throws.
                Assert.Null(Record.Exception(() => fetch.Token));
                Assert.Null(Record.Exception(() => check.Token));

                // The grid posted its first fetch when it was built and it has not run yet. It must
                // not start now, on a grid that has been closed.
                Dispatcher.UIThread.RunJobs();
                Assert.Same(fetch, GetField(grid, "_fetchCts"));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The History document already cancelled its fetch when closed. Its cancel moved into the
    /// shared release with the other kinds, so it is pinned here.
    /// </summary>
    [Fact]
    public void ClosingAHistoryDocumentStillCancelsItsFetch()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var history = SessionHarness.NewHistory();
                session.AddHistorySubTab("History", history);
                var document = Assert.Single(SessionHarness.Documents(session));
                var fetch = SessionHarness.PlantHistoryFetch(history);

                SessionHarness.CloseFromHeader(document);

                Assert.True(fetch.IsCancellationRequested);
                Assert.Empty(SessionHarness.Documents(session));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Get Actual Plan from a file opens a window-level tab that holds a spinner until the query
    /// answers. Closing that tab has to stop the query.
    /// </summary>
    [Fact]
    public void ClosingAWindowLevelLoadingTabCancelsItsRun()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                var run = new CancellationTokenSource();
                var loadingTab = window.AddLoadingTab("Actual Plan", new Grid(), run);
                Assert.False(run.IsCancellationRequested);

                SessionHarness.CloseFromHeader(loadingTab);
                Dispatcher.UIThread.RunJobs();

                Assert.DoesNotContain(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.True(run.IsCancellationRequested,
                    "the Actual Plan tab was closed and its query carried on against the server");
                Assert.Null(Record.Exception(() => run.Token));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// #625: cancelling that query closes its tab, the way the query session's capture does. The
    /// cancel used to land in the error handler and stay on screen as an error.
    /// </summary>
    [Fact]
    public void CancellingAWindowLevelRunClosesItsTab()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                var run = new CancellationTokenSource();
                var progress = new CaptureProgressPanel(window, "Capturing actual plan...", run);
                var loadingTab = window.AddLoadingTab("Actual Plan", progress.Root, run);

                run.Cancel();
                var ended = window.EndCancelledCaptureAsync(loadingTab);
                Dispatcher.UIThread.RunJobs();

                Assert.True(ended.IsCompletedSuccessfully);
                Assert.DoesNotContain(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- #627: a tab does not leave the strip while its capture runs --------------------------

    /// <summary>
    /// #627: Run Repro on a plan file opens an Actual Plan tab, and the run puts its plan in that tab
    /// when it finishes. Detaching the tab mid-run moved the progress panel to a new window and left
    /// the plan to arrive in a tab nobody could see, while the window kept a spinner and a Cancel
    /// button for a run that was over. A running capture is not detached.
    ///
    /// <para>This replaces #626's CancellingARunWhoseTabWasDetachedSaysSoInItsWindow, which detached
    /// a running capture on purpose and checked what the cancel then said to the stranded panel. The
    /// app cannot reach that state any more, so the test proves the rule that removed it: the detach
    /// is refused, nothing is taken off the tab, and a cancel closes the tab the way it always does.</para>
    /// </summary>
    [Fact]
    public void ARunningCaptureTabIsNotDetachedAndCancellingItStillClosesTheTab()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            Window? detached = null;
            try
            {
                var run = new CancellationTokenSource();
                var progress = new CaptureProgressPanel(window, "Capturing actual plan...", run);
                var loadingTab = window.AddLoadingTab("Actual Plan", progress.Root, run);

                detached = window.DetachTabToWindow(loadingTab);

                Assert.Null(detached);
                Assert.Contains(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.Same(progress.Root, loadingTab.Content);
                Assert.Same(loadingTab, window.MainTabControl.SelectedItem);
                Assert.False(run.IsCancellationRequested, "refusing the detach stopped the run");

                run.Cancel();
                var ended = window.EndCancelledCaptureAsync(loadingTab);
                Dispatcher.UIThread.RunJobs();

                Assert.True(ended.IsCompletedSuccessfully);
                Assert.DoesNotContain(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
            }
            finally
            {
                // Only with the rule broken: a detached window is not owned by the main window, so
                // PutAway would leave it open to poison the shared headless session.
                detached?.Close();
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The menu is built once, with the tab, and a capture ends while the tab sits in the strip, so
    /// the item has to be decided when the menu opens, not when it was built. Disabled while the run
    /// is going, and enabled again by the same menu once the plan has replaced the panel.
    /// </summary>
    [Fact]
    public void DetachToWindowIsDisabledWhileTheCaptureRunsAndEnabledOnceItsPlanArrives()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            try
            {
                var run = new CancellationTokenSource();
                var progress = new CaptureProgressPanel(window, "Capturing actual plan...", run);
                var loadingTab = window.AddLoadingTab("Actual Plan", progress.Root, run);
                var detachItem = TabMenuItem(loadingTab, "Detach to Window");

                RightClick(loadingTab);
                Assert.False(detachItem.IsEnabled, "the capture is running and the tab offered to detach");

                // A click that gets through anyway, which a disabled item would stop in the real menu,
                // still leaves the tab where the run will find it.
                detachItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Contains(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.Same(progress.Root, loadingTab.Content);

                // The run finishes: its plan replaces the panel, the swap Run Repro makes.
                var viewer = new PlanViewerControl();
                Assert.True(viewer.LoadPlan(SessionHarness.SamplePlanXml(), "Actual Plan"));
                loadingTab.Content = window.CreatePlanTabContent(viewer);

                RightClick(loadingTab);
                Assert.True(detachItem.IsEnabled, "the menu kept the answer it gave while the run was going");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Once the plan has replaced the panel the tab is an ordinary plan tab, and detaching it works as
    /// it always did: the tab leaves the strip and its content is in the new window.
    /// </summary>
    [Fact]
    public void ATabWhoseCaptureDeliveredItsPlanDetachesAsBefore()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            Window? detached = null;
            try
            {
                var run = new CancellationTokenSource();
                var progress = new CaptureProgressPanel(window, "Capturing actual plan...", run);
                var loadingTab = window.AddLoadingTab("Actual Plan", progress.Root, run);
                Assert.Null(window.DetachTabToWindow(loadingTab));

                var viewer = new PlanViewerControl();
                Assert.True(viewer.LoadPlan(SessionHarness.SamplePlanXml(), "Actual Plan"));
                var planContent = window.CreatePlanTabContent(viewer);
                loadingTab.Content = planContent;

                detached = window.DetachTabToWindow(loadingTab);

                Assert.NotNull(detached);
                Assert.DoesNotContain(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.Contains(planContent, ((DockPanel)detached!.Content!).Children);
            }
            finally
            {
                detached?.Close();
                Dispatcher.UIThread.RunJobs();
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A capture that failed leaves its panel in the tab, showing the error, with no run behind it.
    /// Detaching that moves nothing a run is waiting for, so the tab is free to go again.
    /// </summary>
    [Fact]
    public void ACaptureThatFailedCanBeDetached()
    {
        HeadlessUi.Run(() =>
        {
            var (window, _) = SessionHarness.NewSession();
            Window? detached = null;
            try
            {
                var run = new CancellationTokenSource();
                var progress = new CaptureProgressPanel(window, "Capturing actual plan...", run);
                var loadingTab = window.AddLoadingTab("Actual Plan", progress.Root, run);
                var detachItem = TabMenuItem(loadingTab, "Detach to Window");

                RightClick(loadingTab);
                Assert.False(detachItem.IsEnabled);

                progress.ShowFailure("Msg 208, Level 16, State 1: Invalid object name 'dbo.Missing'.");

                RightClick(loadingTab);
                Assert.True(detachItem.IsEnabled, "the run is over and the item stayed disabled");

                detached = window.DetachTabToWindow(loadingTab);

                Assert.NotNull(detached);
                Assert.DoesNotContain(loadingTab, window.MainTabControl.Items.OfType<TabItem>());
                Assert.Contains(progress.Root, ((DockPanel)detached!.Content!).Children);
            }
            finally
            {
                detached?.Close();
                Dispatcher.UIThread.RunJobs();
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- E8: Escape cancels the run its tab belongs to, and no other --------------------------

    /// <summary>
    /// The first run's tab is still on screen, superseded by the second. Escape pressed on it
    /// cancelled whatever run was current, which is the second one.
    /// </summary>
    [Fact]
    public void EscapeOnATabWhoseRunEndedDoesNotCancelANewerRun()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";

                StartCapture(session);
                var older = Assert.Single(SessionHarness.Documents(session));
                var olderRun = CurrentRun(session);

                StartCapture(session);
                var newerRun = CurrentRun(session);
                Assert.NotSame(olderRun, newerRun);
                Assert.True(olderRun.IsCancellationRequested, "starting a run supersedes the one before it");
                Assert.False(newerRun.IsCancellationRequested);

                PressEscape((Control)older.Content!);

                Assert.False(newerRun.IsCancellationRequested,
                    "Escape on the older run's tab reached past it and cancelled the newer run");
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The tab's own Cancel button follows the same rule as its Escape key.
    /// </summary>
    [Fact]
    public void TheCancelButtonOnATabWhoseRunEndedDoesNotCancelANewerRun()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";

                StartCapture(session);
                var older = Assert.Single(SessionHarness.Documents(session));

                StartCapture(session);
                var newerRun = CurrentRun(session);

                CancelButtonOf(older).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.False(newerRun.IsCancellationRequested,
                    "the older run's Cancel button cancelled the newer run");
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Escape on a tab that is still running its own run stops it. The fix must not have made the
    /// key do nothing.
    /// </summary>
    [Fact]
    public void EscapeOnALoadingTabCancelsItsOwnRun()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);

                var loadingTab = Assert.Single(SessionHarness.Documents(session));
                var run = CurrentRun(session);
                Assert.False(run.IsCancellationRequested);

                PressEscape((Control)loadingTab.Content!);

                Assert.True(run.IsCancellationRequested);
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The session hears every Escape in it. One pressed on a finished plan reached the handler that
    /// cancels the current run and stopped a capture running on another tab.
    /// </summary>
    [Fact]
    public void EscapeOnAFinishedPlanDocumentDoesNotCancelARunningCapture()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var finishedPlan = SessionHarness.OpenPlanDocuments(session, 1)[0];
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);
                var run = CurrentRun(session);

                SessionHarness.PressHeader(session, finishedPlan);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Same(finishedPlan.Content, SessionHarness.DocumentHost(session).Content);

                PressEscape((Control)finishedPlan.Content!);

                Assert.False(run.IsCancellationRequested,
                    "Escape on a finished plan cancelled the capture running on another tab");
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Escape in the editor is how a query started there is stopped, and it still is.
    /// </summary>
    [Fact]
    public void EscapeInTheEditorStillCancelsTheRunningCapture()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);
                var run = CurrentRun(session);

                SessionHarness.EditorSegment(session).IsChecked = true;
                window.UpdateLayout();
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);

                PressEscape(session.QueryEditor);

                Assert.True(run.IsCancellationRequested);
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Escape with the focus somewhere in the session other than the spinner itself, while the
    /// spinner's tab is the one showing, is still about that tab's run.
    /// </summary>
    [Fact]
    public void EscapeAnywhereWhileTheLoadingTabIsShowingCancelsItsRun()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";
                StartCapture(session);
                var run = CurrentRun(session);
                window.UpdateLayout();

                PressEscape(session.FindControl<Button>("ExecuteButton")!);

                Assert.True(run.IsCancellationRequested);
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>The .sqlplan the suite's other chrome tests open.</summary>
    private static string PlanPath() =>
        Path.Combine(System.AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");

    /// <summary>The window-level tab that holds a plan opened from a file.</summary>
    private static TabItem PlanTab(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Single(t => t.Content is DockPanel);

    private static PlanViewerControl PlanViewerOf(TabItem tab) =>
        ((DockPanel)tab.Content!).Children.OfType<PlanViewerControl>().Single();

    private static TabItem TabOf(MainWindow window, QuerySessionControl session) =>
        window.MainTabControl.Items.OfType<TabItem>().Single(t => t.Content == session);

    /// <summary>
    /// Opens the tab's context menu the way a right-click does: Avalonia raises ContextMenu.Opening
    /// from the ContextRequested handler on the control the menu is attached to, not from Open(), so
    /// the event goes to the header. Then closes it, so the next call is a fresh open. The same helper
    /// CopyPathVisibilityTests drives its #472 menu with.
    /// </summary>
    private static void RightClick(TabItem tab)
    {
        var header = (StackPanel)tab.Header!;
        header.RaiseEvent(new ContextRequestedEventArgs());
        header.ContextMenu!.Close();
    }

    private static MenuItem TabMenuItem(TabItem tab, string header) =>
        ((StackPanel)tab.Header!).ContextMenu!.Items
            .OfType<MenuItem>()
            .Single(i => (i.Header as string) == header);

    private static Button RedockButton(Window detached) =>
        ((DockPanel)detached.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single();

    /// <summary>
    /// The id the viewer registered its plan under. Private, because nothing outside the viewer has
    /// a reason to know it; reached by name the way the session harness reaches its fields.
    /// </summary>
    private static string McpSessionId(PlanViewerControl viewer) =>
        (string)typeof(PlanViewerControl)
            .GetField("_mcpSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

    /// <summary>
    /// Starts an actual-plan capture the way Execute does, as far as a test without a SQL Server
    /// can: the loading tab is built before the server is dialled. Callers cancel the run in their
    /// finally, since the pretend server is a port nothing listens on.
    /// </summary>
    private static void StartCapture(QuerySessionControl session)
    {
        var capture = typeof(QuerySessionControl).GetMethod(
            "CaptureAndShowPlan", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _ = (Task)capture.Invoke(session, new object?[] { false, null })!;
    }

    /// <summary>The source the session's most recent run is on, off the session's own field.</summary>
    private static CancellationTokenSource CurrentRun(QuerySessionControl session) =>
        (CancellationTokenSource)GetField(session, "_executionCts")!;

    private static QueryStoreGridControl NewGrid() =>
        new(new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
            new NoCredentials(),
            new ServerUtcOffset(),
            initialDatabase: "master",
            databases: new List<string> { "master" });

    /// <summary>The Cancel button on a loading tab: the last child of the panel inside its container.</summary>
    private static Button CancelButtonOf(TabItem loadingTab) =>
        ((Panel)loadingTab.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single();

    private static void PressEscape(Control target) =>
        SessionHarness.PressKey(target, Key.Escape, KeyModifiers.None);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    /// <summary>Windows-auth credentials so the grid's connection-string build asks for nothing.</summary>
    private sealed class NoCredentials : ICredentialService
    {
        public bool SaveCredential(string serverId, string username, string password) => false;
        public (string Username, string Password)? GetCredential(string serverId) => null;
        public bool DeleteCredential(string serverId) => false;
        public bool CredentialExists(string serverId) => false;
        public bool UpdateCredential(string serverId, string username, string password) => false;
    }
}
