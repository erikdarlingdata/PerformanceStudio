using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #595: a statement whose MemoryGrantInfo reports GrantedMemory="0" showed "0 KB granted, 0 KB
/// used (100%)" in the Runtime Summary — a percentage of nothing, styled the same as an operator
/// that used every byte of a real grant. Expected: say there was no grant, with no percentage,
/// in the same neutral color as every other line that isn't flagging a problem.
/// </summary>
public class RuntimeSummaryMemoryGrantTests
{
    [Fact]
    public void NoMemoryGrant_ShowsNeutralLineWithNoPercentage()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlanWithZeroMemoryGrant();
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var grid = SummaryGrid(viewer);
            var (grantText, grantForeground) = Row(grid, "Memory grant");
            var (_, elapsedForeground) = Row(grid, "Elapsed");

            Assert.DoesNotContain("%", grantText);
            Assert.Contains("No memory grant", grantText);
            // Neutral, same as every other row that passes no brush key — not the WarningBrush
            // the old "0 grant reads as 100% used" math would have colored it, since this
            // fixture's tree really did spill (on the Sort feeding the grant).
            Assert.Equal(elapsedForeground, grantForeground);
        });
    }

    /// <summary>
    /// memory_grant_wait_plan.sqlplan already has a real MemoryGrantInfo and a real spill
    /// (SpillToTempDb on a Sort). Zeroing only the grant/used-memory attributes keeps the spill,
    /// which is what makes the neutral-color assertion meaningful instead of trivially true.
    /// </summary>
    private static PlanViewerControl LoadPlanWithZeroMemoryGrant()
    {
        var path = Path.Combine("Plans", "memory_grant_wait_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        const string granted = "GrantedMemory=\"10851312\"";
        const string maxUsed = "MaxUsedMemory=\"10232840\"";
        Assert.Contains(granted, xml);
        Assert.Contains(maxUsed, xml);
        xml = xml.Replace(granted, "GrantedMemory=\"0\"").Replace(maxUsed, "MaxUsedMemory=\"0\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "memory_grant_wait_plan.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }

    private static Grid SummaryGrid(PlanViewerControl viewer)
    {
        var panel = viewer.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "RuntimeSummaryContent");
        return (Grid)panel.Children.Single();
    }

    private static (string Text, IBrush Foreground) Row(Grid grid, string label)
    {
        var labelBlock = grid.Children.OfType<TextBlock>()
            .First(t => Grid.GetColumn(t) == 0 && t.Text == label);
        var row = Grid.GetRow(labelBlock);
        var valueBlock = grid.Children.OfType<TextBlock>()
            .First(t => Grid.GetColumn(t) == 1 && Grid.GetRow(t) == row);
        return (valueBlock.Text ?? "", valueBlock.Foreground!);
    }
}
