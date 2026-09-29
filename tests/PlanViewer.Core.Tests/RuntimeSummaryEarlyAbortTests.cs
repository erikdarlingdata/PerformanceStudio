using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #613: the early abort reason is part of the optimization result (StatementOptmEarlyAbortReason
/// only exists for a FULL optimization), so the Runtime Summary shows it as a detail of the
/// Optimization row: on the next row, with its label indented and its value in the same column.
/// CE model moved up above Optimization, so Optimization and its reason end the list.
/// </summary>
public class RuntimeSummaryEarlyAbortTests
{
    [Fact]
    public void EarlyAbort_IsNestedUnderOptimization()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan(stripOptimizationLevel: false);
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var grid = SummaryGrid(viewer);
            var ceModel = Label(grid, "CE model");
            var optimization = Label(grid, "Optimization");
            var earlyAbort = Label(grid, "Early abort");

            Assert.Equal(Grid.GetRow(ceModel) + 1, Grid.GetRow(optimization));
            Assert.Equal(Grid.GetRow(optimization) + 1, Grid.GetRow(earlyAbort));
            Assert.Equal(grid.RowDefinitions.Count - 1, Grid.GetRow(earlyAbort));
            Assert.True(earlyAbort.Margin.Left > optimization.Margin.Left,
                $"Early abort label is not indented: {earlyAbort.Margin.Left} vs {optimization.Margin.Left}");
            Assert.Equal("GoodEnoughPlanFound", Value(grid, earlyAbort));
        });
    }

    [Fact]
    public void EarlyAbort_WithoutAnOptimizationRow_IsNotIndented()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan(stripOptimizationLevel: true);
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var grid = SummaryGrid(viewer);
            Assert.DoesNotContain(grid.Children.OfType<TextBlock>(), t => t.Text == "Optimization");

            var earlyAbort = Label(grid, "Early abort");
            var elapsed = Label(grid, "Elapsed");
            Assert.Equal(elapsed.Margin.Left, earlyAbort.Margin.Left);
        });
    }

    /// <summary>
    /// key_lookup_plan.sqlplan is an actual plan with StatementOptmLevel="FULL" and
    /// StatementOptmEarlyAbortReason="GoodEnoughPlanFound". Removing the level leaves a reason
    /// with no row to nest under.
    /// </summary>
    private static PlanViewerControl LoadPlan(bool stripOptimizationLevel)
    {
        var path = Path.Combine("Plans", "key_lookup_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        const string level = "StatementOptmLevel=\"FULL\"";
        Assert.Contains(level, xml);
        Assert.Contains("StatementOptmEarlyAbortReason=\"GoodEnoughPlanFound\"", xml);
        if (stripOptimizationLevel)
            xml = xml.Replace(level, "");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "key_lookup_plan.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }

    private static Grid SummaryGrid(PlanViewerControl viewer)
    {
        var panel = viewer.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "RuntimeSummaryContent");
        return (Grid)panel.Children.Single();
    }

    private static TextBlock Label(Grid grid, string label)
        => grid.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 0 && t.Text == label);

    private static string Value(Grid grid, TextBlock label)
        => grid.Children.OfType<TextBlock>()
            .Single(t => Grid.GetColumn(t) == 1 && Grid.GetRow(t) == Grid.GetRow(label)).Text ?? "";
}
