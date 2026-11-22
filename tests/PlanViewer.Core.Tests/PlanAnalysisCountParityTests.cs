using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using Xunit;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The plan-analysis sentences PerformanceMonitor settled in its #8523 read the same way here. A count of one agrees
/// with its noun: a Nested Loops inner side that read one page reads "Inner side total: 1 logical read.", a Filter
/// whose subtree read one page reads "1 logical read below", and a wait recorded once reads "(1 wait)" in the Wait
/// Stats card and in the Operator Properties panel alike, both as "{time} ms ({count} wait)". Every number in a
/// sentence that carries a figure is written in the invariant culture, so a wait warning, a memory-grant warning, a
/// compile-CPU warning and a UDF warning read the same on a German desktop as on an English one. Each test sets the
/// thread culture for the call and restores it afterwards; the culture is per thread, so a test running beside it is
/// not touched.
/// </summary>
public class PlanAnalysisCountParityTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void NestedLoops_OneLogicalReadOnTheInnerSide_ReadsSingular()
    {
        // An inner-side seek that ran 150,001 times and read one page in all: the detail counts the read as one.
        var inner = new PlanNode { NodeId = 2, PhysicalOp = "Index Seek", LogicalOp = "Index Seek", HasActualStats = true, EstimateRows = 1, ActualRows = 150_001, ActualExecutions = 150_001, ActualLogicalReads = 1 };
        var join = NestedLoops(Scan(nodeId: 1, estimateRows: 150_001, actualRows: 150_001), inner);

        var warning = AnalyzeSingle(join, statementMs: 1_000, "Nested Loops High Executions");

        Assert.Contains("Inner side total: 1 logical read.", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterOperator_OneLogicalReadBelow_ReadsSingular()
    {
        // A Filter over a scan that read one page in 50 ms: the impact line counts the read as one.
        var scan = new PlanNode { NodeId = 1, PhysicalOp = "Index Scan", LogicalOp = "Index Scan", HasActualStats = true, EstimateRows = 10, ActualRows = 10, ActualExecutions = 1, ActualLogicalReads = 1, ActualElapsedMs = 50 };
        var filter = new PlanNode { NodeId = 0, PhysicalOp = "Filter", LogicalOp = "Filter", HasActualStats = true, EstimateRows = 5, ActualRows = 5, ActualExecutions = 1, Predicate = "[t].[c]=(1)" };
        filter.Children.Add(scan);
        scan.Parent = filter;

        var warning = AnalyzeSingle(filter, statementMs: 1_000, "Filter Operator");

        Assert.Contains("1 logical read below", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitWarning_OnAGermanDesktop_WritesItsTimeCountAndLatencyInOneConvention()
    {
        var stmt = Statement();
        stmt.WaitStats.Add(new WaitStatInfo { WaitType = "PAGEIOLATCH_SH", WaitTimeMs = 1_500, WaitCount = 1_000 });

        var warnings = OnAGermanDesktop(() => Analyze(stmt));
        var wait = Assert.Single(warnings, w => w.WarningType == "Wait: PAGEIOLATCH_SH");

        Assert.Equal("PAGEIOLATCH_SH Observed 1,500 ms across 1,000 waits. Effective latency: 1.5 ms per wait.", wait.Message);
    }

    [Theory]
    [InlineData(0, "Granted 1,536 MB but the query used none of it.")]
    [InlineData(102_400, "Granted 1,536 MB but only used 100 MB (15x overestimate).")]
    public void ExcessiveMemoryGrant_OnAGermanDesktop_WritesItsMegabytesInOneConvention(long usedKB, string expected)
    {
        var stmt = Statement();
        stmt.MemoryGrant = new MemoryGrantInfo { GrantedMemoryKB = 1_572_864, MaxUsedMemoryKB = usedKB, HasMaxUsedMemory = true };

        var warnings = OnAGermanDesktop(() => Analyze(stmt));
        var grant = Assert.Single(warnings, w => w.WarningType == "Excessive Memory Grant");

        Assert.StartsWith(expected, grant.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryGrantWait_OnAGermanDesktop_WritesItsMillisecondsInOneConvention()
    {
        var stmt = Statement();
        stmt.MemoryGrant = new MemoryGrantInfo { GrantedMemoryKB = 1_024, GrantWaitTimeMs = 1_500 };

        var warnings = OnAGermanDesktop(() => Analyze(stmt));
        var wait = Assert.Single(warnings, w => w.WarningType == "Memory Grant Wait");

        Assert.StartsWith("Query waited 1,500ms for a memory grant", wait.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HighCompileCpu_OnAGermanDesktop_WritesItsMillisecondsInOneConvention()
    {
        var stmt = Statement();
        stmt.CompileCPUMs = 1_500;

        var warnings = OnAGermanDesktop(() => Analyze(stmt));
        var compile = Assert.Single(warnings, w => w.WarningType == "High Compile CPU");

        Assert.StartsWith("Query took 1,500ms of CPU just to compile a plan", compile.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementUdfExecution_OnAGermanDesktop_WritesBothTimesInOneConvention()
    {
        var stmt = Statement();
        stmt.QueryUdfElapsedTimeMs = 1_500;
        stmt.QueryUdfCpuTimeMs = 1_200;

        var warnings = OnAGermanDesktop(() => Analyze(stmt));
        var udf = Assert.Single(warnings, w => w.WarningType == "UDF Execution");

        Assert.StartsWith("Scalar UDF cost in this statement: 1,500ms elapsed, 1,200ms CPU.", udf.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertiesPanel_WaitRow_OneWait_ReadsSingular()
    {
        HeadlessUi.Run(() =>
        {
            // The recorded plan's waits are rewritten to a single wait each, which no checked-in plan carries.
            var xml = Regex.Replace(FixtureXml("isnull_plan.sqlplan"), "WaitCount=\"\\d+\"", "WaitCount=\"1\"");
            var rows = PropertiesPanelRows(xml);

            Assert.Equal("500 ms (1 wait)", rows["PAGEIOLATCH_SH"]);
        });
    }

    [Fact]
    public void PropertiesPanel_WaitRow_OnAGermanDesktop_WritesEveryNumberInOneConvention()
    {
        HeadlessUi.Run(() =>
        {
            var rows = OnAGermanDesktop(() => PropertiesPanelRows(FixtureXml("isnull_plan.sqlplan")));

            Assert.Equal("426 ms (1,409 waits)", rows["SOS_SCHEDULER_YIELD"]);
        });
    }

    private static PlanStatement Statement() => new()
    {
        RootNode = Scan(nodeId: 1, estimateRows: 10, actualRows: 10),
        QueryTimeStats = new QueryTimeInfo { ElapsedTimeMs = 10_000, CpuTimeMs = 1_000 }
    };

    private static IEnumerable<PlanNode> Descendants(PlanNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Descendants));

    /// <summary>Runs the analyzer and the scorer, the way the pipeline does, and returns every warning the statement and its nodes carry.</summary>
    private static List<PlanWarning> Analyze(PlanStatement stmt)
    {
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        var nodeWarnings = stmt.RootNode is null ? [] : Descendants(stmt.RootNode).SelectMany(n => n.Warnings);
        return stmt.PlanWarnings.Concat(nodeWarnings).ToList();
    }

    private static PlanWarning AnalyzeSingle(PlanNode root, long statementMs, string warningType)
    {
        var stmt = new PlanStatement { RootNode = root, QueryTimeStats = new QueryTimeInfo { ElapsedTimeMs = statementMs } };
        PlanAnalyzer.Analyze(new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] });
        return Assert.Single(root.Warnings, w => w.WarningType == warningType);
    }

    private static PlanNode Scan(int nodeId, double estimateRows, long actualRows) => new()
    {
        NodeId = nodeId,
        PhysicalOp = "Index Scan",
        LogicalOp = "Index Scan",
        HasActualStats = true,
        EstimateRows = estimateRows,
        ActualRows = actualRows,
        ActualExecutions = 1
    };

    /// <summary>A Nested Loops join over the two children, with the parent links the row-estimate helpers walk.</summary>
    private static PlanNode NestedLoops(PlanNode outer, PlanNode inner)
    {
        var join = new PlanNode { NodeId = 0, PhysicalOp = "Nested Loops", LogicalOp = "Inner Join", HasActualStats = true, EstimateRows = outer.EstimateRows, ActualRows = inner.ActualRows, ActualExecutions = 1 };
        join.Children.Add(outer);
        join.Children.Add(inner);
        outer.Parent = join;
        inner.Parent = join;
        return join;
    }

    /// <summary>
    /// Loads the plan into a viewer, opens the Operator Properties panel for the statement's root operator, and
    /// returns the panel's rows by label from its own row model (<c>_propertySections</c>), as the panel tests do.
    /// </summary>
    private static Dictionary<string, string> PropertiesPanelRows(string xml)
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "waits.sqlplan"), viewer.LastLoadError);
        var window = new Window { Content = viewer, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var statementField = typeof(PlanViewerControl).GetField("_currentStatement", Instance);
        Assert.True(statementField is not null, "PlanViewerControl._currentStatement no longer exists under that name - this test's reflection anchor moved.");
        var statement = Assert.IsType<PlanStatement>(statementField!.GetValue(viewer));
        Assert.NotNull(statement.RootNode);

        var show = typeof(PlanViewerControl).GetMethod("ShowPropertiesPanel", Instance);
        Assert.True(show is not null, "PlanViewerControl.ShowPropertiesPanel no longer exists under that name - this test's reflection anchor moved.");
        show!.Invoke(viewer, new object[] { statement.RootNode });

        var sections = typeof(PlanViewerControl).GetField("_propertySections", Instance);
        Assert.True(sections is not null, "PlanViewerControl._propertySections no longer exists under that name - this test's reflection anchor moved.");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in (IEnumerable)sections!.GetValue(viewer)!)
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

    /// <summary>
    /// Runs <paramref name="read"/> with the thread's culture set to German, which groups with a dot and uses a
    /// decimal comma, and restores the culture afterwards. The culture is per thread, so a test running alongside
    /// is not touched.
    /// </summary>
    private static T OnAGermanDesktop<T>(Func<T> read)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static string FixtureXml(string fixture)
    {
        var path = System.IO.Path.Combine("Plans", fixture);
        Assert.True(System.IO.File.Exists(path), $"Test plan not found: {path}");
        return System.IO.File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }
}
