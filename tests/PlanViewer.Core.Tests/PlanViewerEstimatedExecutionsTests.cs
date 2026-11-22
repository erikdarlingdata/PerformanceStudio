using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using PlanViewer.Web;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #677: an operator runs once, plus once per rebind and once per rewind, and every surface of the plan viewer counts it
/// that way and prints its estimates with the same decimals. The Properties panel's Est. Executions and Est. Rows All
/// Execs, the arrow tooltip's Estimated Number of Rows for All Executions, the node tooltip's Estimated Rows and the HTML
/// export agree on an inner-side spool with 10 rebinds and 990 rewinds: 1,001 executions, 10,010 rows in all. The App's
/// private builders are reached by reflection; the Web panel is not referenced by this project, so it is pinned to the
/// shared helpers the way <see cref="PlanRowAccuracyTests.WebNodeLabel_UsesTheSharedFormatter"/> pins the node label.
/// </summary>
public class PlanViewerEstimatedExecutionsTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    /* An inner-side lazy spool: 10 rows per run, rebound 10 times and rewound 990 times, so 1,001 runs and 10,010 rows in all. */
    private static PlanNode Spool(double rebinds = 10, double rewinds = 990) => new()
    {
        NodeId = 7,
        PhysicalOp = "Table Spool",
        LogicalOp = "Lazy Spool",
        EstimateRows = 10,
        EstimateRebinds = rebinds,
        EstimateRewinds = rewinds,
    };

    [Fact]
    public void ThePropertiesPanel_CountsRewindsAsExecutions_AndMultipliesRowsByThem()
    {
        Dictionary<string, string> rows = null!;
        HeadlessUi.Run(() =>
        {
            var control = new PlanViewerControl();
            ShowProperties(control, Spool());
            rows = PropertyRows(control);
        });

        Assert.Equal(1001.0.ToString("N0", CultureInfo.CurrentCulture), rows["Est. Executions"]);
        Assert.Equal(10010.0.ToString("N1", CultureInfo.CurrentCulture), rows["Est. Rows All Execs"]);
    }

    /// <summary>Rebind and rewind estimates are fractional, so the count is too: 10.5 rebinds is 11.5 runs, printed with
    /// its decimal rather than rounded to 12 beside a row total that was computed from 11.5.</summary>
    [Fact]
    public void ThePropertiesPanel_PrintsAFractionalCountWithItsDecimals()
    {
        Dictionary<string, string> rows = null!;
        HeadlessUi.Run(() =>
        {
            var control = new PlanViewerControl();
            ShowProperties(control, Spool(rebinds: 10.5, rewinds: 0));
            rows = PropertyRows(control);
        });

        Assert.Equal(11.5.ToString("#,##0.#", CultureInfo.CurrentCulture), rows["Est. Executions"]);
        Assert.Equal(115.0.ToString("N1", CultureInfo.CurrentCulture), rows["Est. Rows All Execs"]);
    }

    [Fact]
    public void ThePropertiesPanel_AndTheArrowTooltip_AgreeOnRowsForAllExecutions()
    {
        string panel = null!, arrow = null!;
        HeadlessUi.Run(() =>
        {
            var control = new PlanViewerControl();
            ShowProperties(control, Spool());
            panel = PropertyRows(control)["Est. Rows All Execs"];
            arrow = TooltipRows(Build(control, "BuildEdgeTooltipContent", Spool()))["Estimated Number of Rows for All Executions"];
        });

        Assert.Equal(panel, arrow);
    }

    /// <summary>The same estimate prints with the same decimals wherever it prints: 10 rows reads "10.0" in the panel, on the
    /// arrow and in the node tooltip, never "10" on one and "10.0" on the next.</summary>
    [Fact]
    public void EverySurface_PrintsTheSameEstimateTheSameWay()
    {
        string panel = null!, arrow = null!, node = null!;
        HeadlessUi.Run(() =>
        {
            var control = new PlanViewerControl();
            ShowProperties(control, Spool());
            panel = PropertyRows(control)["Est. Rows Per Exec"];
            arrow = TooltipRows(Build(control, "BuildEdgeTooltipContent", Spool()))["Estimated Number of Rows Per Execution"];
            node = TooltipRows(Build(control, "BuildNodeTooltipContent", Spool(), null))["Estimated Rows"];
        });

        Assert.Equal(10.0.ToString("N1", CultureInfo.CurrentCulture), panel);
        Assert.Equal(panel, arrow);
        Assert.Equal(panel, node);
    }

    /// <summary>The HTML export of an estimated plan prints the spool's rows for all executions from the same count, so
    /// the download agrees with the viewer that made it.</summary>
    [Fact]
    public void TheHtmlExport_CountsRewindsAsExecutions()
    {
        var html = ExportEstimatedSpoolPlan(rebinds: "10", rewinds: "990");

        var culture = CultureInfo.CurrentCulture;
        Assert.Contains(
            $"{10010.0.ToString("N1", culture)} est. rows over {1001.0.ToString("#,##0.#", culture)} est. execs ({10.0.ToString("N1", culture)} per exec)",
            html);
        Assert.DoesNotContain(">10 est. rows<", html);
    }

    /// <summary>A fractional count prints with its decimal: 10.5 rebinds is 11.5 runs, and the line says so rather than
    /// rounding it to 12 beside a row total that was computed from 11.5.</summary>
    [Fact]
    public void TheHtmlExport_PrintsAFractionalCountWithItsDecimals()
    {
        var html = ExportEstimatedSpoolPlan(rebinds: "10.5", rewinds: "0");

        var culture = CultureInfo.CurrentCulture;
        Assert.Contains(
            $"{115.0.ToString("N1", culture)} est. rows over {11.5.ToString("#,##0.#", culture)} est. execs ({10.0.ToString("N1", culture)} per exec)",
            html);
        Assert.DoesNotContain("12 est. execs", html);
    }

    /// <summary>A count that prints as one takes the single-run line. 0.0183 rewinds (an outer side estimated at 1.0183
    /// rows) is 1.0183 runs, which prints as "1", so the spool reads "10.0 est. rows" like every other single-run
    /// operator rather than "10.2 est. rows over 1 est. execs", a line that contradicts itself.</summary>
    [Fact]
    public void TheHtmlExport_ACountThatPrintsAsOne_TakesTheSingleRunLine()
    {
        var html = ExportEstimatedSpoolPlan(rebinds: "0", rewinds: "0.0183");

        var culture = CultureInfo.CurrentCulture;
        Assert.DoesNotContain("over 1 est. execs", html);
        Assert.DoesNotContain($"{10.183.ToString("N1", culture)} est. rows", html);
        // Every row line reads as it does when the spool has no rewinds at all.
        Assert.Equal(RowLines(ExportEstimatedSpoolPlan(rebinds: "0", rewinds: "0")), RowLines(html));
    }

    private static List<string> RowLines(string html)
        => Regex.Matches(html, "<span class=\"op-rows\">[^<]*</span>").Select(m => m.Value).ToList();

    /// <summary>The Web node tooltip (the node's title text) prints the estimate through the shared formatter, so it reads
    /// "10.0" like the Operator Properties panel and the HTML export, not "10".</summary>
    [Fact]
    public void WebNodeTooltip_PrintsTheEstimateThroughTheFormatter()
    {
        var tooltip = PlanViewFormat.BuildTooltip(Spool());

        Assert.Contains($"Estimated rows: {10.0.ToString("N1", CultureInfo.CurrentCulture)}", tooltip);
    }

    /// <summary>The Web viewer's razor is not compiled by this project, so this pins its Operator Properties panel and its
    /// canvas node label to the shared helpers instead of rendering them.</summary>
    [Fact]
    public void WebOperatorProperties_UseTheSharedHelpers()
    {
        var razor = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "PlanViewer.Web", "Pages", "OperatorPropertiesPanel.razor"));

        Assert.Contains("MetricFormatter.FormatEstimatedExecutions(RowEstimateHelper.GetEstimatedExecutions(Node))", razor);
        Assert.Contains("RowEstimateHelper.GetEstimatedRowsAllExecutions(Node)", razor);
        Assert.Contains("MetricFormatter.FormatEstimatedRows(Node.EstimateRows)", razor);
        Assert.Contains("MetricFormatter.FormatEstimatedRows(Node.EstimatedRowsRead)", razor);
        Assert.Contains("MetricFormatter.FormatEstimatedRows(Node.EstimateRowsWithoutRowGoal)", razor);
        Assert.DoesNotContain("1 + Node.EstimateRebinds", razor);
        Assert.DoesNotContain("Node.EstimatedRowsRead.ToString(\"N0\")", razor);
        Assert.DoesNotContain("Node.EstimateRowsWithoutRowGoal.ToString(\"N0\")", razor);
    }

    [Fact]
    public void WebCanvasNodeLabel_UsesTheSharedFormatter()
    {
        var razor = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "PlanViewer.Web", "Pages", "Index.razor"));

        Assert.Contains("{MetricFormatter.FormatEstimatedRows(node.EstimateRows)} est. rows", razor);
        Assert.DoesNotContain("{node.EstimateRows:N0}", razor);
    }

    private static string ExportEstimatedSpoolPlan(string rebinds, string rewinds)
    {
        var plan = ShowPlanParser.Parse(EstimatedSpoolPlan(rebinds, rewinds));
        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        var result = ResultMapper.Map(plan, "estimated_spool.sqlplan");
        return HtmlExporter.Export(result, TextFormatter.Format(result));
    }

    /// <summary>An estimated plan (no runtime counters): a Nested Loops join whose inner side is the lazy spool above a Constant Scan.</summary>
    private static string EstimatedSpoolPlan(string rebinds, string rewinds) =>
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\" Version=\"1.6\" Build=\"17.0.1000.7\"><BatchSequence><Batch><Statements>"
        + "<StmtSimple StatementText=\"SELECT 1\" StatementId=\"1\" StatementCompId=\"1\" StatementType=\"SELECT\" StatementSubTreeCost=\"10\" StatementEstRows=\"10\">"
        + "<QueryPlan DegreeOfParallelism=\"1\" CachedPlanSize=\"16\">"
        + "<RelOp NodeId=\"0\" PhysicalOp=\"Nested Loops\" LogicalOp=\"Inner Join\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"10\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"11\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\"><OutputList /><NestedLoops Optimized=\"0\">"
        + "<RelOp NodeId=\"1\" PhysicalOp=\"Constant Scan\" LogicalOp=\"Constant Scan\" EstimateRows=\"1000\" EstimatedTotalSubtreeCost=\"0.001\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"9\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\"><OutputList /><ConstantScan /></RelOp>"
        + $"<RelOp NodeId=\"7\" PhysicalOp=\"Table Spool\" LogicalOp=\"Lazy Spool\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"9\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"9\" Parallel=\"0\" EstimateRebinds=\"{rebinds}\" EstimateRewinds=\"{rewinds}\"><OutputList /><Spool>"
        + "<RelOp NodeId=\"8\" PhysicalOp=\"Constant Scan\" LogicalOp=\"Constant Scan\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"0.001\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"9\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\"><OutputList /><ConstantScan /></RelOp>"
        + "</Spool></RelOp></NestedLoops></RelOp>"
        + "</QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>";

    private static void ShowProperties(PlanViewerControl control, PlanNode node)
    {
        var show = typeof(PlanViewerControl).GetMethod("ShowPropertiesPanel", Instance);
        Assert.True(show is not null, "PlanViewerControl.ShowPropertiesPanel no longer exists under that name - this test's reflection anchor moved.");
        show!.Invoke(control, new object[] { node });
    }

    private static Border Build(PlanViewerControl control, string builder, params object?[] args)
    {
        var build = typeof(PlanViewerControl).GetMethod(builder, Instance);
        Assert.True(build is not null, $"PlanViewerControl.{builder} no longer exists under that name - this test's reflection anchor moved.");
        return Assert.IsType<Border>(build!.Invoke(control, args));
    }

    /// <summary>The Properties panel's rows by label, read from the panel's own row model (<c>_propertySections</c>).</summary>
    private static Dictionary<string, string> PropertyRows(PlanViewerControl control)
    {
        var field = typeof(PlanViewerControl).GetField("_propertySections", Instance);
        Assert.True(field is not null, "PlanViewerControl._propertySections no longer exists under that name - this test's reflection anchor moved.");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in (IEnumerable)field!.GetValue(control)!)
        {
            foreach (var row in (IEnumerable)section.GetType().GetProperty("Rows")!.GetValue(section)!)
            {
                var label = (string?)row.GetType().GetProperty("Label")!.GetValue(row);
                var value = (string?)row.GetType().GetProperty("Value")!.GetValue(row);
                if (!string.IsNullOrEmpty(label) && value is not null)
                    rows[label] = value;
            }
        }

        return rows;
    }

    /// <summary>A tooltip's rows by label: each is a Grid with a label TextBlock in column 0 and a value TextBlock in column 1.
    /// The node tooltip's labels end in ": ", which is trimmed so both tooltips read by the same keys.</summary>
    private static Dictionary<string, string> TooltipRows(Border border)
    {
        var stack = Assert.IsType<StackPanel>(border.Child);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var grid in stack.Children.OfType<Grid>())
        {
            var cells = grid.Children.OfType<TextBlock>().OrderBy(Grid.GetColumn).Select(t => t.Text ?? "").ToList();
            if (cells.Count == 2)
                rows[cells[0].TrimEnd().TrimEnd(':')] = cells[1];
        }

        return rows;
    }

    private static string SolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "PlanViewer.sln")))
            current = current.Parent;
        Assert.True(current is not null, "PlanViewer.sln was not found above the test output directory.");
        return current!.FullName;
    }
}
