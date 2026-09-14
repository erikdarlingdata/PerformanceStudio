using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A new Query tab used to be a wall of empty editor: no sign of what the app can do, and the
/// three things a user actually wants there — open a plan, paste plan XML, connect — reachable
/// only from a menu they had no reason to look in. The empty state offers them, and gets out of
/// the way the instant the session holds anything.
///
/// <para>What is pinned here is the appearing and disappearing, because that is what can trap
/// typing if it is wrong, plus the two things that rot quietly: the hit-testing of clickable
/// text (a null background answers clicks on glyphs only — the trap this repo has fallen into
/// before), and the printed shortcuts, which are a second copy of the File menu's gestures.</para>
/// </summary>
public class EmptyQueryTabStateTests
{
    [Fact]
    public void AFreshQueryTabShowsTheEmptyState_AndTypingTakesItAway()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            Assert.True(Overlay(session).IsVisible, "nothing typed, no sub-tabs — a fresh tab");

            session.QueryEditor.Text = "select 1;";
            Assert.False(Overlay(session).IsVisible, "the editor is in use");

            session.QueryEditor.Text = "";
            Assert.True(Overlay(session).IsVisible,
                "a buffer emptied back out is the same state a fresh tab is in");
        });
    }

    /// <summary>
    /// The half that is easy to forget: an empty editor is not an empty session. Run a query,
    /// read the plan, come back to the editor tab having typed nothing — an overlay waiting
    /// there would be offering to get started on a session that already has.
    /// </summary>
    [Fact]
    public void AnOpenPlanKeepsTheEmptyStateAwayEvenWithAnEmptyEditor()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            session.OnQueryStorePlansSelected(null, new List<QueryStorePlan>
            {
                new()
                {
                    QueryId = 1,
                    PlanId = 1,
                    QueryText = "select 1;",
                    PlanXml = PlanXml("row_goal_plan.sqlplan")
                }
            });

            Assert.Equal("", session.QueryEditor.Text);
            Assert.False(Overlay(session).IsVisible, "this session holds a plan");
        });
    }

    /// <summary>
    /// Every row is clickable across its whole width, not just where its glyphs happen to fall.
    /// A control with a null background hit-tests only what it draws, so a press in the gap
    /// between two words would sail past the row and land on the panel behind it.
    /// </summary>
    [Fact]
    public void EveryClickableRowHasABackgroundToBeHitOn()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.LoadPlanFile(PlanPath("row_goal_plan.sqlplan"));

            var session = NewSession(window);
            var rows = ActionRows(session).ToList();

            /* Open a plan, Paste plan XML, Connect to a server, and at least one recent plan —
               the one loaded above. */
            Assert.True(rows.Count >= 4, $"expected the three actions and a recent plan, found {rows.Count}");
            Assert.All(rows, row => Assert.NotNull(row.Background));
        });
    }

    [Fact]
    public void RecentPlansAreOfferedByPath_AndCappedAtFive()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();

            var path = PlanPath("key_lookup_plan.sqlplan");
            window.LoadPlanFile(path);

            var session = NewSession(window);
            var recent = RecentPlanRows(session).ToList();

            Assert.Contains(path, recent.Select(row => row.Tag as string));
            Assert.True(recent.Count <= 5, "the empty state is a starting point, not the whole File menu");
        });
    }

    /// <summary>
    /// The shortcuts printed next to the two File menu actions are a copy of that menu's
    /// gestures, and a copy drifts. Changing either gesture should fail here rather than leave
    /// the empty state quietly teaching the wrong keys.
    /// </summary>
    [Fact]
    public void ThePrintedShortcutsAreTheOnesTheFileMenuBinds()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            var session = NewSession(window);

            var printed = session.FindControl<StackPanel>("EmptyStateFileActions")!
                .GetLogicalDescendants()
                .OfType<TextBlock>()
                .Where(text => text.Classes.Contains("gesture"))
                .Select(text => text.Text)
                .ToList();

            Assert.Equal(
                new[]
                {
                    window.FindControl<MenuItem>("OpenPlanMenuItem")!.InputGesture!.ToString(),
                    window.FindControl<MenuItem>("PastePlanXmlMenuItem")!.InputGesture!.ToString()
                },
                printed);
        });
    }

    private static QuerySessionControl NewSession(MainWindow window)
    {
        window.NewQuery_Click(window, new RoutedEventArgs());
        return window.FindControl<TabControl>("MainTabControl")!.Items
            .OfType<TabItem>()
            .Select(tab => tab.Content)
            .OfType<QuerySessionControl>()
            .Last();
    }

    private static Border Overlay(QuerySessionControl session) =>
        session.FindControl<Border>("EmptyStateOverlay")!;

    /// <summary>Every clickable row in the panel: the three actions, then the recent plans.</summary>
    private static IEnumerable<Border> ActionRows(QuerySessionControl session) =>
        Overlay(session).GetLogicalDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("action"));

    private static IEnumerable<Border> RecentPlanRows(QuerySessionControl session) =>
        session.FindControl<StackPanel>("EmptyStateRecentPlans")!.Children.OfType<Border>();

    private static string PlanPath(string name) =>
        Path.Combine(System.AppContext.BaseDirectory, "Plans", name);

    /// <summary>SSMS writes plan files as UTF-16 and declares it; the parser wants the declaration
    /// to match what it is handed. Same substitution the other plan-loading tests make.</summary>
    private static string PlanXml(string name) =>
        File.ReadAllText(PlanPath(name)).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
}
