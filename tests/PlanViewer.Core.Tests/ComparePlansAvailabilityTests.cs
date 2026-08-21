using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #447: Compare Plans stayed disabled when the two plans lived in two different query sessions,
/// which is the ordinary case — run a query, rewrite it, run it again in a second tab. The button's
/// enablement counted only the session's OWN plan tabs, while the picker behind it had always been
/// able to see across sessions. The reporter had to save a plan and reopen it to get at a comparison
/// the app could already do.
///
/// The scenario is built from plan FILES rather than executed queries deliberately: getting a plan
/// into a session needs a live SQL Server, and the defect does not require one. What it requires is
/// plans existing somewhere OTHER than the session whose button is being judged, which two file tabs
/// provide exactly.
/// </summary>
public class ComparePlansAvailabilityTests
{
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

    private static string PlanPath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "Plans", name);

    private static System.Collections.Generic.IEnumerable<QuerySessionControl> Sessions(MainWindow window) =>
        window.FindControl<TabControl>("MainTabControl")!.Items
            .OfType<TabItem>()
            .Select(tab => tab.Content)
            .OfType<QuerySessionControl>();

    private static Button CompareButton(QuerySessionControl session) =>
        session.FindControl<Button>("ComparePlansButton")!;
}
