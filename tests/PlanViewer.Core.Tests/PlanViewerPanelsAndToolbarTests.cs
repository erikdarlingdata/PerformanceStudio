using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// What the plan viewer shows when a plan did not load, and how its panels and its Parameters card share a small space.
/// The toolbar buttons that act on a drawn plan are enabled only while one is drawn. With the Statements and Properties
/// panels both open in a 900 px viewer the canvas keeps room for an operator. In the Parameters card the value columns keep
/// their width whatever the parameter's name takes, and the grid is never wider than its card. The control is laid out in a
/// headless window and read as laid out. Ported from erikdarlingdata/PerformanceMonitor#8538.
/// </summary>
public class PlanViewerPanelsAndToolbarTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private const string Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>A parameter name of 37 characters, wider than a Parameters card.</summary>
    private const string LongParameterName = "@TheThirtySevenCharacterParameterName";

    /// <summary>The narrowest canvas that shows an operator whole.</summary>
    private const double CanvasMinimum = 240;

    private const double StatementsMinimum = 200;
    private const double PropertiesMinimum = 280;

    /// <summary>The least a Parameters card value column keeps: a hover target for a cut value's tooltip.</summary>
    private const double ValueColumnMinimum = 40;

    [Fact]
    public void TheButtonsThatActOnAPlan_AreEnabledOnlyWhileOneIsDrawn()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var failed = new PlanViewerControl();
            Assert.False(failed.LoadPlan("not a plan", "Pasted Plan"), "text that is not a plan loaded as one");
            var failedWindow = new Window { Content = failed, Width = 1280, Height = 800 };
            failedWindow.Show();
            SettleLayout(failedWindow);
            problems.AddRange(ButtonsNotIn(failed, enabled: false, "after text that does not parse"));
            failedWindow.Close();

            var loaded = LoadPlan(Plan(Statement(1, Scan(0))));
            var loadedWindow = new Window { Content = loaded, Width = 1280, Height = 800 };
            loadedWindow.Show();
            SettleLayout(loadedWindow);
            problems.AddRange(ButtonsNotIn(loaded, enabled: true, "with a plan drawn"));
            loadedWindow.Close();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void WithTheStatementsAndPropertiesPanelsOpen_A900PixelViewer_KeepsRoomForAnOperator()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var viewer = LoadPlan(Plan(Statement(1, Scan(0))));
            var window = new Window { Content = viewer, Width = 900, Height = 600 };
            window.Show();
            SettleLayout(window);
            OpenProperties(viewer);
            SettleLayout(window);

            var statements = Named<Control>(viewer, "StatementsPanel");
            var properties = Named<Control>(viewer, "PropertiesPanel");
            var scroller = Named<ScrollViewer>(viewer, "PlanScrollViewer");
            Assert.True(statements.IsVisible, "the Statements panel is closed for a plan with a statement");
            Assert.True(properties.IsVisible, "the Properties panel did not open");

            if (scroller.Bounds.Width < CanvasMinimum - 0.5)
                problems.Add($"the plan canvas is {scroller.Bounds.Width:F0} px wide in a {viewer.Bounds.Width:F0} px viewer with the Statements panel ({statements.Bounds.Width:F0} px) and the Properties panel ({properties.Bounds.Width:F0} px) open: it keeps {CanvasMinimum} px");
            if (statements.Bounds.Width < StatementsMinimum - 0.5)
                problems.Add($"the Statements panel is {statements.Bounds.Width:F0} px wide; it narrows no further than {StatementsMinimum} px");
            if (properties.Bounds.Width < PropertiesMinimum - 0.5)
                problems.Add($"the Properties panel is {properties.Bounds.Width:F0} px wide; it narrows no further than {PropertiesMinimum} px");
            var propertiesRight = properties.TranslatePoint(new Point(properties.Bounds.Width, 0), viewer)?.X ?? double.NaN;
            if (!(propertiesRight <= viewer.Bounds.Width + 0.5))
                problems.Add($"the Properties panel ends at {propertiesRight:F0} px in a {viewer.Bounds.Width:F0} px viewer");
            window.Close();

            /* The narrowing is the small viewer's alone: with room for all three, the Statements panel takes its 640 px. */
            var wide = LoadPlan(Plan(Statement(1, Scan(0))));
            var wideWindow = new Window { Content = wide, Width = 1700, Height = 900 };
            wideWindow.Show();
            SettleLayout(wideWindow);
            OpenProperties(wide);
            SettleLayout(wideWindow);
            var wideStatements = Named<Control>(wide, "StatementsPanel");
            if (Math.Abs(wideStatements.Bounds.Width - 640) > 0.5)
                problems.Add($"the Statements panel is {wideStatements.Bounds.Width:F0} px wide in a {wide.Bounds.Width:F0} px viewer with the Properties panel open; with room for the canvas it takes its 640 px");
            wideWindow.Close();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void ALongParameterName_LeavesTheValueColumnsTheirWidth()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var viewer = LoadPlan(Plan(Statement(1, Scan(0), LongNameParameter())));
            var window = new Window { Content = viewer, Width = 1280, Height = 900 };
            window.Show();
            SettleLayout(window);
            Named<Expander>(viewer, "InsightsExpander").IsExpanded = true;
            SettleLayout(window);

            var content = Named<StackPanel>(viewer, "ParametersContent");
            var grid = content.Children.OfType<Grid>().Single();
            var cells = grid.Children.OfType<TextBlock>().ToList();
            var card = content.GetLogicalAncestors().OfType<ScrollViewer>().First();
            Assert.True(card.Viewport.Width > 0, "the Parameters card is not laid out");

            /* The grid is as wide as the card shows: a column past the card's edge is cut there. */
            if (grid.Bounds.Width > card.Viewport.Width + 0.5)
                problems.Add($"the Parameters grid is {grid.Bounds.Width:F0} px wide in a card that shows {card.Viewport.Width:F0} px: its last column is cut at the card's edge");

            foreach (var header in new[] { "Compiled", "Runtime" })
            {
                var cell = cells.SingleOrDefault(c => Grid.GetRow(c) == 0 && c.Text == header);
                if (cell is null)
                {
                    problems.Add($"the Parameters card has no \"{header}\" column for a parameter with a compiled and a runtime value");
                    continue;
                }

                var column = grid.ColumnDefinitions[Grid.GetColumn(cell)];
                if (column.ActualWidth < ValueColumnMinimum - 0.5)
                    problems.Add($"the \"{header}\" column is {column.ActualWidth:F0} px wide beside a {LongParameterName.Length}-character parameter name; it keeps {ValueColumnMinimum} px");
                var right = cell.TranslatePoint(new Point(cell.Bounds.Width, 0), card)?.X ?? double.NaN;
                if (!(right <= card.Viewport.Width + 0.5))
                    problems.Add($"the \"{header}\" header ends at {right:F0} px in a card that shows {card.Viewport.Width:F0} px");
            }

            /* The name is what gives: it is drawn narrower than its one-line width, and its tooltip carries it whole. */
            var name = cells.Single(c => c.Text == LongParameterName);
            var oneLine = new TextBlock { Text = name.Text, FontFamily = name.FontFamily, FontSize = name.FontSize, FontWeight = name.FontWeight };
            oneLine.Measure(Size.Infinity);
            if (name.Bounds.Width > oneLine.DesiredSize.Width - 0.5)
                problems.Add($"the {LongParameterName.Length}-character parameter name is drawn whole ({name.Bounds.Width:F0} px) in a {card.Viewport.Width:F0} px card with two value columns");
            if (ToolTip.GetTip(name) as string != LongParameterName)
                problems.Add($"the cut parameter name's tooltip is \"{ToolTip.GetTip(name)}\"");
            window.Close();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void NarrowingAWideViewerTo900_WithBothPanelsOpen_FitsThePanelsAgain()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var viewer = LoadPlan(Plan(Statement(1, Scan(0))));
            var host = new Border { Width = 1700, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = viewer };
            var window = new Window { Content = host, Width = 1700, Height = 900 };
            window.Show();
            SettleLayout(window);
            OpenProperties(viewer);
            SettleLayout(window);

            var statements = Named<Control>(viewer, "StatementsPanel");
            var properties = Named<Control>(viewer, "PropertiesPanel");
            var scroller = Named<ScrollViewer>(viewer, "PlanScrollViewer");
            Assert.True(Math.Abs(statements.Bounds.Width - 640) < 0.5, $"the Statements panel is {statements.Bounds.Width:F0} px wide in a 1700 px viewer");

            host.Width = 900;
            SettleLayout(window);

            Assert.True(Math.Abs(viewer.Bounds.Width - 900) < 0.5, $"the viewer is {viewer.Bounds.Width:F0} px wide in a 900 px host");
            var propertiesRight = properties.TranslatePoint(new Point(properties.Bounds.Width, 0), viewer)?.X ?? double.NaN;
            if (!(propertiesRight <= 900 + 0.5))
                problems.Add($"the Properties panel ends at {propertiesRight:F0} px in a viewer narrowed from 1700 px to 900 px");
            if (statements.Bounds.Width > 640 - 0.5)
                problems.Add($"the Statements panel is still {statements.Bounds.Width:F0} px wide in a viewer narrowed from 1700 px to 900 px");
            if (scroller.Bounds.Width < CanvasMinimum - 0.5)
                problems.Add($"the plan canvas is {scroller.Bounds.Width:F0} px wide in a viewer narrowed from 1700 px to 900 px: it keeps {CanvasMinimum} px");
            window.Close();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    /// <summary>A 600 px viewer cannot hold both panels at their minimums and a 240 px canvas, so one panel shows at a time,
    /// the one opened last.</summary>
    [Fact]
    public void InAViewerTooNarrowForBothPanels_ThePanelOpenedLastShows_AndTheCanvasKeepsItsRoom()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var viewer = LoadPlan(Plan(Statement(1, Scan(0))));
            var window = new Window { Content = viewer, Width = 600, Height = 600 };
            window.Show();
            SettleLayout(window);
            var statements = Named<Control>(viewer, "StatementsPanel");
            var properties = Named<Control>(viewer, "PropertiesPanel");
            var scroller = Named<ScrollViewer>(viewer, "PlanScrollViewer");
            Assert.True(Math.Abs(viewer.Bounds.Width - 600) < 0.5, $"the viewer is {viewer.Bounds.Width:F0} px wide in a 600 px window");
            Assert.True(statements.IsVisible, "the Statements panel is closed for a plan with a statement");

            OpenProperties(viewer);
            SettleLayout(window);
            Assert.True(properties.IsVisible, "the Properties panel did not open");
            if (statements.IsVisible)
                problems.Add($"the Statements panel ({statements.Bounds.Width:F0} px) stays open beside the Properties panel ({properties.Bounds.Width:F0} px) in a 600 px viewer, which cannot hold both and the canvas");
            if (scroller.Bounds.Width < CanvasMinimum - 0.5)
                problems.Add($"the plan canvas is {scroller.Bounds.Width:F0} px wide in a 600 px viewer with the Properties panel open: it keeps {CanvasMinimum} px");
            var propertiesRight = properties.TranslatePoint(new Point(properties.Bounds.Width, 0), viewer)?.X ?? double.NaN;
            if (!(propertiesRight <= 600 + 0.5))
                problems.Add($"the Properties panel ends at {propertiesRight:F0} px in a 600 px viewer");

            Named<Button>(viewer, "StatementsButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SettleLayout(window);
            if (!statements.IsVisible)
                problems.Add("the Statements button did not reopen the statement list");
            if (statements.IsVisible && properties.IsVisible)
                problems.Add($"the Properties panel ({properties.Bounds.Width:F0} px) stays open beside the reopened Statements panel ({statements.Bounds.Width:F0} px) in a 600 px viewer");
            if (scroller.Bounds.Width < CanvasMinimum - 0.5)
                problems.Add($"the plan canvas is {scroller.Bounds.Width:F0} px wide in a 600 px viewer with the statement list reopened: it keeps {CanvasMinimum} px");
            window.Close();

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void AClickOnTheStatementsSplitter_IsNotADraggedWidth()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan(Plan(Statement(1, Scan(0))));
            var host = new Border { Width = 900, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = viewer };
            var window = new Window { Content = host, Width = 1700, Height = 900 };
            window.Show();
            SettleLayout(window);
            OpenProperties(viewer);
            SettleLayout(window);
            var statements = Named<Control>(viewer, "StatementsPanel");
            Assert.True(statements.Bounds.Width < 640 - 0.5, $"the Statements panel is {statements.Bounds.Width:F0} px wide beside the Properties panel in a 900 px viewer; this test starts from a narrowed panel");

            // A pointer press and release on the splitter with no movement.
            Named<GridSplitter>(viewer, "StatementsSplitter").RaiseEvent(
                new Avalonia.Input.VectorEventArgs { RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, Vector = default });
            host.Width = 1700;
            SettleLayout(window);

            Assert.True(Math.Abs(statements.Bounds.Width - 640) < 0.5,
                $"the Statements panel is {statements.Bounds.Width:F0} px wide in a 1700 px viewer after a click on its splitter at 900 px: a click that moves nothing is not a dragged width, so the panel takes its 640 px");
            window.Close();
        });
    }

    /// <summary>A plan over the parser's size limit and a plan with no statement to draw are plans all the same: the viewer
    /// cannot draw them, and Save .sqlplan is what takes them to a tool that can.</summary>
    [Fact]
    public void SaveStaysEnabled_ForAPlanTheViewerDoesNotDraw()
    {
        HeadlessUi.Run(() =>
        {
            var problems = new List<string>();

            var oversize = new PlanViewerControl();
            Assert.False(oversize.LoadPlan(Plan(Statement(1, Scan(0))) + new string(' ', (16 * 1024 * 1024) + 1), "big.sqlplan"), "a plan over the size limit loaded");
            Assert.Contains("size limit", oversize.LastLoadError ?? "", StringComparison.Ordinal);
            problems.AddRange(SaveAloneIsEnabled(oversize, "for a plan over the parser's size limit"));

            var empty = new PlanViewerControl();
            Assert.False(empty.LoadPlan(Plan(), "empty.sqlplan"), "a plan with no statement loaded as drawn");
            problems.AddRange(SaveAloneIsEnabled(empty, "for a plan with no statement to draw"));

            var text = new PlanViewerControl();
            Assert.False(text.LoadPlan("not a plan", "Pasted Plan"), "text that is not a plan loaded as one");
            problems.AddRange(ButtonsNotIn(text, enabled: false, "after text that does not parse"));

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    /// <summary>What is wrong when Save .sqlplan is not the one enabled button of the five.</summary>
    private static IEnumerable<string> SaveAloneIsEnabled(PlanViewerControl viewer, string when)
    {
        var buttons = viewer.GetLogicalDescendants().OfType<Button>().ToList();
        foreach (var tip in new[] { "Zoom In", "Zoom Out", "Zoom to Fit", "Save plan as .sqlplan file", "Show/hide minimap" })
        {
            var button = buttons.FirstOrDefault(b => ToolTip.GetTip(b) as string == tip);
            Assert.True(button is not null, $"the plan viewer has no \"{tip}\" button");
            var expected = tip == "Save plan as .sqlplan file";
            if (button!.IsEnabled != expected)
                yield return $"{when}, the \"{tip}\" button is {(expected ? "disabled" : "enabled")}";
        }
    }

    /// <summary>The toolbar and canvas buttons that act on a drawn plan and are not in the <paramref name="enabled"/> state.</summary>
    private static IEnumerable<string> ButtonsNotIn(PlanViewerControl viewer, bool enabled, string when)
    {
        var buttons = viewer.GetLogicalDescendants().OfType<Button>().ToList();
        foreach (var tip in new[] { "Zoom In", "Zoom Out", "Zoom to Fit", "Save plan as .sqlplan file", "Show/hide minimap" })
        {
            var button = buttons.FirstOrDefault(b => ToolTip.GetTip(b) as string == tip);
            Assert.True(button is not null, $"the plan viewer has no \"{tip}\" button");
            if (button!.IsEnabled != enabled)
                yield return $"{when}, the \"{tip}\" button is {(enabled ? "disabled" : "enabled")}";
        }
    }

    /// <summary>Opens the Properties panel on the drawn statement's root operator, as selecting that operator does.</summary>
    private static void OpenProperties(PlanViewerControl viewer)
    {
        var statementField = typeof(PlanViewerControl).GetField("_currentStatement", Instance);
        Assert.True(statementField is not null, "PlanViewerControl._currentStatement no longer exists under that name - this test's reflection anchor moved.");
        var statement = Assert.IsType<PlanStatement>(statementField!.GetValue(viewer));
        Assert.NotNull(statement.RootNode);

        var show = typeof(PlanViewerControl).GetMethod("ShowPropertiesPanel", Instance);
        Assert.True(show is not null, "PlanViewerControl.ShowPropertiesPanel no longer exists under that name - this test's reflection anchor moved.");
        show!.Invoke(viewer, new object[] { statement.RootNode! });
    }

    /// <summary>Runs layout until it is quiet: a width set from a size-changed or layout-updated handler takes effect on
    /// the next pass, which the dispatcher runs on its own in the app.</summary>
    private static void SettleLayout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T Named<T>(Control root, string name) where T : Control
    {
        var match = root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(match is not null, $"{typeof(T).Name} '{name}' is not in the plan viewer");
        return match!;
    }

    private static PlanViewerControl LoadPlan(string xml)
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "plan.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }

    private static string Plan(params string[] statements) =>
        $"<ShowPlanXML xmlns=\"{Ns}\" Version=\"1.564\" Build=\"16.0.4000.1\"><BatchSequence><Batch><Statements>{string.Concat(statements)}</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static string Statement(int id, string rootOperator, string parameterList = "") =>
        $"<StmtSimple StatementText=\"SELECT {id} FROM dbo.t{id}\" StatementId=\"{id}\" StatementCompId=\"{id}\" StatementType=\"SELECT\" StatementSubTreeCost=\"0.0032\" StatementEstRows=\"1\" QueryHash=\"0x{id:X16}\" QueryPlanHash=\"0x{id:X16}\">"
        + $"<QueryPlan CachedPlanSize=\"16\" CompileTime=\"1\" CompileCPU=\"1\" CompileMemory=\"100\">{rootOperator}{parameterList}</QueryPlan></StmtSimple>";

    /// <summary>One datetime parameter with a compiled and a runtime value, under <see cref="LongParameterName"/>.</summary>
    private static string LongNameParameter() =>
        $"<ParameterList><ColumnReference Column=\"{LongParameterName}\" ParameterDataType=\"datetime\" ParameterCompiledValue=\"'2026-01-01 00:00:00.000'\" ParameterRuntimeValue=\"'2026-02-01 00:00:00.000'\"/></ParameterList>";

    private static string Scan(int nodeId) =>
        $"<RelOp NodeId=\"{nodeId}\" PhysicalOp=\"Table Scan\" LogicalOp=\"Table Scan\" EstimateRows=\"1\" EstimateIO=\"0.003\" EstimateCPU=\"0.0001\" AvgRowSize=\"9\" EstimatedTotalSubtreeCost=\"0.0032\" TableCardinality=\"100\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\" EstimatedExecutionMode=\"Row\">"
        + $"<OutputList/><TableScan Ordered=\"0\" ForcedIndex=\"0\" ForceScan=\"0\" NoExpandHint=\"0\" Storage=\"RowStore\"><DefinedValues/><Object Database=\"[db]\" Schema=\"[dbo]\" Table=\"[t{nodeId}]\" IndexKind=\"Heap\" Storage=\"RowStore\"/></TableScan></RelOp>";
}
