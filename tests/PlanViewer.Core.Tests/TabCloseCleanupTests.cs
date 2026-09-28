using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Mcp;
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
}
