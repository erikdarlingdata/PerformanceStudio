using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The Plan Insights panel in a window narrower and shorter than the plan viewer was drawn for: a
/// 700 px window, and the main window's 640x480 minimum. The five insight cards stay reachable,
/// inside the viewer or by the strip's horizontal scroll bar; the Server Context card stops at its
/// 360 px cap and wraps its text inside itself; and the panel starts collapsed in a viewer too short
/// to leave the plan graph most of the height with the panel open, expanded in a tall one, and as
/// the person left it after they have toggled it.
///
/// <para>Headless text metrics: a character is FontSize DIP wide and a line 1.0898 em tall (see
/// <see cref="HeadlessUi"/>), so the edition string below needs several thousand DIP on one line.</para>
/// </summary>
public class InsightsStripLayoutTests
{
    private const double CardCap = 360;

    /// <summary>The MinWidths of the Missing Indexes, Parameters and Wait Stats columns: 200 + 200 + 280.</summary>
    private const double StarColumnMinimums = 680;

    /// <summary>At every width the grid is bounded: as wide as the strip, or as wide as its two content-sized
    /// cards plus the other three columns' minimums when the strip is narrower than that. So the Wait Stats
    /// card never sizes to its longest row. With this fixture's long edition both content-sized cards sit at
    /// the 360 px cap, so the bound is 1,400 px: at 1280 the strip scrolls by the 120 px it must, and at 1600
    /// and 1920 it has nothing to scroll and the trailing Wait Stats card reaches the viewer's right edge.</summary>
    [Theory]
    [InlineData(700, 600)]
    [InlineData(640, 480)]
    [InlineData(1280, 800)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public void TheInsightCards_StayReachable_InANarrowWindow(double width, double height)
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan();
            viewer.Metadata = LongEditionMetadata();
            var window = new Window { Content = viewer, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();

            // The height fit may start the panel collapsed in a short viewer; the cards are measured open.
            var expander = Named<Expander>(viewer, "InsightsExpander");
            expander.IsExpanded = true;
            window.UpdateLayout();

            var strip = viewer.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "InsightsStrip");
            var cards = InsightCards(viewer).Where(c => c.IsVisible).ToList();
            Assert.True(cards.Count >= 4, $"{cards.Count} visible insight cards found");

            var viewerWidth = viewer.Bounds.Width;
            var canScroll = strip is not null && strip.Extent.Width > strip.Viewport.Width + 0.5;
            var problems = new List<string>();
            var rightEdges = new List<double>();
            foreach (var card in cards)
            {
                var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), viewer)?.X ?? double.NaN;
                rightEdges.Add(right);
                if (double.IsNaN(right) || (right > viewerWidth + 0.5 && !canScroll))
                    problems.Add($"a card ends at {right:F0} px in a {viewerWidth:F0} px viewer with no way to scroll to it (strip {(strip is null ? "absent" : $"extent {strip.Extent.Width:F0}, viewport {strip.Viewport.Width:F0}")})");
            }
            /* The grid's width is the strip's, or the two content-sized columns plus the star columns' 680 px
               of minimums when the strip is narrower than that; never the Wait Stats card's longest row. */
            var grid = Named<Grid>(viewer, "InsightsGrid");
            var contentColumns = grid.ColumnDefinitions[0].ActualWidth + grid.ColumnDefinitions[1].ActualWidth;
            var stripWidth = strip?.Viewport.Width ?? viewerWidth;
            var bound = Math.Max(stripWidth, contentColumns + StarColumnMinimums);
            if (Math.Abs(grid.Bounds.Width - bound) > 0.5)
                problems.Add($"the insights grid is {grid.Bounds.Width:F0} px wide in a {stripWidth:F0} px strip whose two content-sized cards take {contentColumns:F0} px: it is bounded at {bound:F0} px (the strip, or those cards plus {StarColumnMinimums} px of minimums) so the Wait Stats card fills the remaining width and its rows ellipsize");

            /* Where the strip holds its columns' minimums it has nothing to scroll, and the last card ends at
               the strip's own right edge (the strip sits a pixel inside the viewer on each side). */
            var columnsFit = contentColumns + StarColumnMinimums <= stripWidth + 0.5;
            var stripRight = strip is null ? viewerWidth : strip.TranslatePoint(new Point(strip.Bounds.Width, 0), viewer)?.X ?? double.NaN;
            if (columnsFit && canScroll)
                problems.Add($"the strip scrolls (extent {strip!.Extent.Width:F0} px) in a {stripWidth:F0} px strip that holds its columns' minimums ({contentColumns:F0} + {StarColumnMinimums} px)");
            if (columnsFit && Math.Abs(rightEdges.Max() - stripRight) > 0.5)
                problems.Add($"the last card ends at {rightEdges.Max():F0} px in a strip that ends at {stripRight:F0} px: the Wait Stats card does not fill the remaining width");

            /* The Server row is as long as the edition makes it. Inside the card it is shown whole: on one
               line when it fits, else wrapped onto more lines, never cut off where the card ends. */
            var serverCard = Named<Border>(viewer, "ServerContextBorder");
            var editionRow = viewer.GetLogicalDescendants().OfType<TextBlock>()
                .Single(t => t.Text is not null && t.Text.Contains(EditionTail, StringComparison.Ordinal));
            var oneLine = new TextBlock { Text = editionRow.Text, FontSize = editionRow.FontSize, FontFamily = editionRow.FontFamily };
            oneLine.Measure(Size.Infinity);
            var cardWidth = serverCard.Bounds.Width;
            var staysOneLine = editionRow.Bounds.Height < oneLine.DesiredSize.Height * 1.5;
            if (cardWidth > CardCap + 0.5)
                problems.Add($"the Server Context card is {cardWidth:F0} px wide; a card stops at {CardCap} px so that its text wraps inside it instead of widening the strip");
            if (oneLine.DesiredSize.Width > cardWidth + 0.5 && staysOneLine)
                problems.Add($"the edition row needs {oneLine.DesiredSize.Width:F0} px on one line in a {cardWidth:F0} px card and does not wrap (text {editionRow.Bounds.Width:F0}x{editionRow.Bounds.Height:F0} px): its tail is cut off");

            Assert.True(problems.Count == 0, $"At {width}x{height}:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void TheRuntimeSummaryRows_WrapInsideTheirCard()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan();
            var window = new Window { Content = viewer, Width = 700, Height = 600 };
            window.Show();
            window.UpdateLayout();

            var rows = Named<StackPanel>(viewer, "RuntimeSummaryContent").GetLogicalDescendants().OfType<TextBlock>().ToList();
            Assert.NotEmpty(rows);
            var rigid = rows.Where(t => t.TextWrapping != TextWrapping.Wrap).Select(t => $"\"{t.Text}\"").ToList();
            Assert.True(rigid.Count == 0, $"{rigid.Count} of {rows.Count} Runtime Summary texts do not wrap: {string.Join(", ", rigid)}");
        });
    }

    [Theory]
    [InlineData(700, 600, false)]
    [InlineData(640, 480, false)]
    [InlineData(1280, 800, true)]
    public void TheInsightsPanel_StartsCollapsedInAShortViewer_AndExpandedInATallOne(double width, double height, bool expanded)
    {
        HeadlessUi.Run(() =>
        {
            // The plan loads before the first layout pass, so the fit waits for the viewer's first real height.
            var viewer = LoadPlan();
            var window = new Window { Content = viewer, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();

            var expander = Named<Expander>(viewer, "InsightsExpander");
            var planGrid = Named<Grid>(viewer, "PlanGrid");
            Assert.True(expanded == expander.IsExpanded,
                $"In a {width}x{height} window the viewer is {viewer.Bounds.Height:F0} px tall, Plan Insights is {(expander.IsExpanded ? "expanded" : "collapsed")} and the plan graph has {planGrid.Bounds.Height:F0} px; expected the panel {(expanded ? "expanded" : "collapsed")}");
        });
    }

    [Fact]
    public void TheInsightsPanel_FitsAtOnce_WhenThePlanLoadsIntoALaidOutViewer()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = new PlanViewerControl();
            var window = new Window { Content = viewer, Width = 700, Height = 600 };
            window.Show();
            window.UpdateLayout();

            Assert.True(viewer.LoadPlan(PlanXml(), PlanName), viewer.LastLoadError);
            window.UpdateLayout();

            var expander = Named<Expander>(viewer, "InsightsExpander");
            Assert.False(expander.IsExpanded, $"Plan Insights is expanded in a viewer {viewer.Bounds.Height:F0} px tall");
        });
    }

    [Theory]
    [InlineData(700, 600, true)]
    [InlineData(1280, 800, false)]
    public void TheInsightsPanel_StaysAsThePersonLeftIt_WhenAnotherPlanLoads(double width, double height, bool personsChoice)
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan();
            var window = new Window { Content = viewer, Width = width, Height = height };
            window.Show();
            window.UpdateLayout();

            var expander = Named<Expander>(viewer, "InsightsExpander");
            expander.IsExpanded = personsChoice;
            window.UpdateLayout();

            Assert.True(viewer.LoadPlan(PlanXml(), PlanName), viewer.LastLoadError);
            window.UpdateLayout();

            Assert.Equal(personsChoice, expander.IsExpanded);
        });
    }

    private const string PlanName = "memory_grant_wait_plan.sqlplan";
    private const string EditionTail = "with a very long edition string";

    private static string PlanXml()
    {
        var path = Path.Combine("Plans", PlanName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static PlanViewerControl LoadPlan()
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(PlanXml(), PlanName), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }

    private static ServerMetadata LongEditionMetadata() => new()
    {
        ServerName = "SQL01",
        Edition = "Enterprise Edition: Core-based Licensing " + string.Concat(Enumerable.Repeat(EditionTail + " ", 12)),
        ProductVersion = "16.0.1000.6",
        ProductLevel = "RTM",
        CpuCount = 8,
        PhysicalMemoryMB = 65536,
        MaxDop = 4,
        CostThresholdForParallelism = 50,
        MaxServerMemoryMB = 49152,
    };

    private static T Named<T>(Control root, string name) where T : Control
    {
        var match = root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(match is not null, $"{typeof(T).Name} '{name}' is not in the plan viewer");
        return match!;
    }

    /// <summary>The insight cards: the Borders that are direct children of the insights grid, the one Grid under the panel with five columns.</summary>
    private static IEnumerable<Border> InsightCards(PlanViewerControl viewer)
    {
        var panel = Named<Border>(viewer, "InsightsPanel");
        var grid = panel.GetLogicalDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 5);
        return grid.Children.OfType<Border>();
    }
}
