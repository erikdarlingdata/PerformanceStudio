using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #447: Compare Plans stayed disabled when the two plans lived in two different query sessions,
/// which is the ordinary case — run a query, rewrite it, run it again in a second tab. The button's
/// enablement counted only the session's OWN plan tabs, while the picker behind it had always been
/// able to see across sessions. The reporter had to save a plan and reopen it to get at a comparison
/// the app could already do.
///
/// <para><b>The first version of this file is why the issue was reopened.</b> It built its scenario
/// out of plan FILES on purpose, reasoning that getting a plan into a session needed a live SQL
/// Server and that the defect did not require one. The second half of that was true and the first
/// half was the bug: a plan file opens a window-level tab, which is the one path that was already
/// recomputing. Every test passed against a build in which running two queries — the thing being
/// reported — still left both buttons dead.</para>
///
/// <para>So the tests below drive the paths a plan actually arrives by. What still needs a server is
/// the round trip that produces plan XML, and only that: the landing step each path performs with
/// the XML in hand is reachable directly, and is where the whole defect lived.</para>
/// </summary>
public class ComparePlansAvailabilityTests
{
    /// <summary>
    /// The report, verbatim: a query run in one tab, another query run in a second tab, Compare
    /// disabled in both. Driven through the same <c>ShowCapturedPlan</c> both execution paths use
    /// once the server has answered.
    /// </summary>
    [Fact]
    public void RunningAQueryInEachOfTwoSessionsOffersCompareInBoth()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            var first = NewSession(window);
            var second = NewSession(window);

            RunQuery(first, "row_goal_plan.sqlplan", "Plan 1");

            Assert.All(new[] { first, second }, session => Assert.False(
                CompareButton(session).IsEnabled,
                "one plan cannot be compared against anything"));

            RunQuery(second, "key_lookup_plan.sqlplan", "Plan 1");

            Assert.All(new[] { first, second }, session => Assert.True(
                CompareButton(session).IsEnabled,
                "a query has now run in each session, which is the whole of the report"));
        });
    }

    /// <summary>
    /// Two queries run in the SAME session, which is the case the pre-#449 arithmetic did handle.
    /// Here because that arithmetic no longer exists — the count comes from the window now, and a
    /// window-wide count has its own way of getting a single session wrong.
    /// </summary>
    [Fact]
    public void RunningTwoQueriesInOneSessionOffersCompareThere()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            RunQuery(session, "row_goal_plan.sqlplan", "Plan 1");
            RunQuery(session, "key_lookup_plan.sqlplan", "Plan 2");

            Assert.True(CompareButton(session).IsEnabled);
        });
    }

    /// <summary>
    /// The Query Store path, which opens its plans by adding tabs rather than filling one in, and a
    /// plan tab being closed again. Both used to say so with a call written out at the site; they
    /// now go through the same watcher as everything else, so they need pinning where they did not
    /// before.
    /// </summary>
    [Fact]
    public void QueryStorePlansOfferCompare_AndClosingOneTakesItAway()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            session.OnQueryStorePlansSelected(null, new List<QueryStorePlan>
            {
                QueryStorePlanFrom(11, "row_goal_plan.sqlplan"),
                QueryStorePlanFrom(22, "key_lookup_plan.sqlplan")
            });

            Assert.True(CompareButton(session).IsEnabled,
                "two Query Store plans are open in this session");

            ClosePlanTab(session);

            Assert.False(CompareButton(session).IsEnabled,
                "one of the two was closed, so there is nothing left to compare against");
        });
    }

    /// <summary>
    /// Get Actual Plan on a window-level plan tab, which produces its plan the same way an executed
    /// query does — into a tab that has been showing a spinner since the query was sent, and so is
    /// not an addition to anything.
    /// </summary>
    [Fact]
    public void AnActualPlanArrivingInAnExistingWindowTabIsNoticed()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            window.LoadPlanFile(PlanPath("row_goal_plan.sqlplan"));
            var session = NewSession(window);

            Assert.False(CompareButton(session).IsEnabled);

            var tabs = window.FindControl<TabControl>("MainTabControl")!;
            var spinnerTab = new TabItem { Header = "Actual Plan", Content = new Grid() };
            tabs.Items.Add(spinnerTab);

            var viewer = new PlanViewerControl();
            Assert.True(viewer.LoadPlan(PlanXml("key_lookup_plan.sqlplan"), "Actual Plan"));
            spinnerTab.Content = window.CreatePlanTabContent(viewer);

            Assert.True(CompareButton(session).IsEnabled,
                "the window gained a second plan without gaining a tab");
        });
    }

    [Fact]
    public void ASessionOffersCompareWhenThePlansAreElsewhereInTheWindow()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            window.LoadPlanFile(PlanPath("row_goal_plan.sqlplan"));
            window.LoadPlanFile(PlanPath("key_lookup_plan.sqlplan"));
            window.NewQuery_Click(window, new RoutedEventArgs());

            /* None of these sessions hold plans of their own — precisely the state that used to
               disable the button, and precisely the state the reporter was in. Asserted across every
               session rather than one, because the fix has to reach all of them. */
            var sessions = Sessions(window).ToList();
            Assert.NotEmpty(sessions);
            Assert.All(sessions, session => Assert.Empty(session.GetPlanTabs()));
            Assert.All(sessions, session => Assert.True(
                CompareButton(session).IsEnabled,
                "two plans are open in this window, so every session should offer to compare them"));
        });
    }

    [Fact]
    public void ASingleOpenPlanIsStillNotEnoughToCompare()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            window.LoadPlanFile(PlanPath("row_goal_plan.sqlplan"));
            window.NewQuery_Click(window, new RoutedEventArgs());

            Assert.All(Sessions(window), session => Assert.False(
                CompareButton(session).IsEnabled,
                "one plan cannot be compared against anything"));
        });
    }

    /// <summary>
    /// The part that needed a window-wide refresh rather than a per-session one: a plan opening in
    /// one place has to light the button up everywhere else, including in sessions that existed
    /// before it arrived.
    /// </summary>
    [Fact]
    public void OpeningASecondPlanEnablesCompareInASessionThatAlreadyExisted()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            window.NewQuery_Click(window, new RoutedEventArgs());
            window.LoadPlanFile(PlanPath("row_goal_plan.sqlplan"));

            var sessions = Sessions(window).ToList();
            Assert.NotEmpty(sessions);
            Assert.All(sessions, session => Assert.False(CompareButton(session).IsEnabled));

            window.LoadPlanFile(PlanPath("key_lookup_plan.sqlplan"));

            Assert.All(sessions, session => Assert.True(CompareButton(session).IsEnabled,
                "the session existed before the second plan did, and must still notice it"));
        });
    }

    /// <summary>
    /// Runs a query in a session as far as a test without a SQL Server can.
    ///
    /// <para>The tab holding the progress spinner is opened first, exactly as the execution paths
    /// open it, and the session is then handed plan XML from a fixture in place of what the server
    /// would have returned. Everything after that — building the viewer, loading the plan into it,
    /// and assigning it over the spinner — is the session's own code, and the assignment is the
    /// statement #447 was about.</para>
    /// </summary>
    private static void RunQuery(QuerySessionControl session, string planFileName, string tabLabel)
    {
        var subTabs = SubTabs(session);
        var spinnerTab = new TabItem { Header = tabLabel, Content = new Grid() };
        subTabs.Items.Add(spinnerTab);
        subTabs.SelectedItem = spinnerTab;

        session.ShowCapturedPlan(spinnerTab, PlanXml(planFileName), tabLabel, "select 1;");
    }

    /// <summary>
    /// Closes the session's first plan tab through its own close button, rather than reaching into
    /// the collection — the removal paths are production code too, and they lost their hand-written
    /// refresh along with the paths that add.
    /// </summary>
    private static void ClosePlanTab(QuerySessionControl session)
    {
        var tab = SubTabs(session).Items
            .OfType<TabItem>()
            .First(t => t.Content is PlanViewerControl);

        var closeButton = ((StackPanel)tab.Header!).Children.OfType<Button>().Last();
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static QueryStorePlan QueryStorePlanFrom(long id, string planFileName) =>
        new()
        {
            QueryId = id,
            PlanId = id,
            QueryText = "select 1;",
            PlanXml = PlanXml(planFileName)
        };

    private static QuerySessionControl NewSession(MainWindow window)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        return Sessions(window).Last();
    }

    private static string PlanPath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "Plans", name);

    /// <summary>SSMS writes plan files as UTF-16 and declares it; the parser wants the declaration
    /// to match what it is handed. Same substitution the other plan-loading tests make.</summary>
    private static string PlanXml(string name) =>
        File.ReadAllText(PlanPath(name)).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

    private static IEnumerable<QuerySessionControl> Sessions(MainWindow window) =>
        window.FindControl<TabControl>("MainTabControl")!.Items
            .OfType<TabItem>()
            .Select(tab => tab.Content)
            .OfType<QuerySessionControl>();

    private static TabControl SubTabs(QuerySessionControl session) =>
        session.FindControl<TabControl>("SubTabControl")!;

    private static Button CompareButton(QuerySessionControl session) =>
        session.FindControl<Button>("ComparePlansButton")!;
}
