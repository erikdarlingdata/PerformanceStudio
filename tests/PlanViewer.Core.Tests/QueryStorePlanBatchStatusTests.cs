using System.Collections.Generic;
using System.Linq;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A Query Store batch load's status must be written after the whole batch, because every
/// successful plan selects its new sub-tab and selecting a sub-tab clears the status strip.
///
/// <para>The failure this pins: a bad plan in the middle of a batch reported its error, the next
/// plan's tab selection wiped it, and the all-plans-loaded summary was suppressed because not
/// all plans loaded — so the user asked for three tabs, got two, and the strip said nothing.
/// The seam was two separate changes composing: load failures became auto-clearing error
/// statuses, and sub-tab selection began clearing the strip.</para>
/// </summary>
public class QueryStorePlanBatchStatusTests
{
    [Fact]
    public void AFailureInTheMiddleOfABatchSurvivesTheLaterSuccesses()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            session.OnQueryStorePlansSelected(null, new List<QueryStorePlan>
            {
                QueryStorePlanFrom(11, "row_goal_plan.sqlplan"),
                BrokenQueryStorePlan(22),
                QueryStorePlanFrom(33, "key_lookup_plan.sqlplan")
            });

            Assert.Equal(2, session.GetPlanTabs().Count());

            var status = StatusText(session).Text ?? "";
            Assert.Contains("Loaded 2 of 3", status);
            Assert.Contains("QS 22 / 22", status);
        });
    }

    [Fact]
    public void ACleanBatchReportsEveryPlanLoaded()
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

            Assert.Equal(2, session.GetPlanTabs().Count());
            Assert.Equal("2 Query Store plans loaded", StatusText(session).Text);
        });
    }

    [Fact]
    public void ASingleFailedPlanReportsItsOwnError()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            session.OnQueryStorePlansSelected(null, new List<QueryStorePlan>
            {
                BrokenQueryStorePlan(11)
            });

            Assert.Empty(session.GetPlanTabs());

            var status = StatusText(session).Text ?? "";
            Assert.StartsWith("Couldn't load QS 11 / 11", status);
        });
    }

    private static QueryStorePlan QueryStorePlanFrom(long id, string planFileName) =>
        new()
        {
            QueryId = id,
            PlanId = id,
            QueryText = "select 1;",
            PlanXml = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Plans", planFileName))
                .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"")
        };

    /// <summary>Blank XML: the same shape a Query Store row with a purged plan hands back.</summary>
    private static QueryStorePlan BrokenQueryStorePlan(long id) =>
        new()
        {
            QueryId = id,
            PlanId = id,
            QueryText = "select 1;",
            PlanXml = ""
        };

    private static QuerySessionControl NewSession(MainWindow window)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        var tabs = window.FindControl<TabControl>("MainTabControl")!;
        TabItem last = null!;
        foreach (var item in tabs.Items)
            if (item is TabItem tab && tab.Content is QuerySessionControl)
                last = tab;
        return (QuerySessionControl)last.Content!;
    }

    private static TextBlock StatusText(QuerySessionControl session) =>
        session.FindControl<TextBlock>("StatusText")!;
}
