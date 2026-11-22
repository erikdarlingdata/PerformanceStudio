using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A count in the analysis text agrees with its noun: one thread reads "1 thread", four read
/// "4 threads", and the index script's header reads "1 index" or "2 indexes". No message hands
/// the reader a "(s)" to resolve. A Key Lookup that ran once is graded "over 1 lookup", a lazy
/// spool with one cache hit reads "1 rewind (cache hit)", and the Wait Stats card reads "across
/// 1 wait type" and "1 wait". Every number in those sentences is written in the invariant
/// culture, so the text reads the same on a German desktop as on an English one.
/// </summary>
public class CountOfOneTextTests
{
    [Theory]
    [InlineData("excellent-parallel-spill.sqlplan", "Spill to TempDb", "Spill level 0, 1 thread")]
    [InlineData("excellent-parallel-spill.sqlplan", "Spill to TempDb", "Spill level 0, 4 threads")]
    public void StandaloneSpill_CountsItsThreads(string fixture, string warningType, string expected)
    {
        // The standalone SpillToTempDb message carries nothing after the thread count when the
        // plan reports no grant and no writes, so the whole message is the pin.
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze(fixture), warningType);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message == expected);
    }

    [Theory]
    [InlineData("param-sniffing-posttypeid2.sqlplan", "Sort Spill", "Sort spill level 8, 1 thread. Granted:")]
    [InlineData("memory_grant_wait_plan.sqlplan", "Sort Spill", "Sort spill level 1, 8 threads. Granted:")]
    [InlineData("row-count-spool-slow.sqlplan", "Hash Spill", "Hash spill level 1, 6 threads. Granted:")]
    public void SortAndHashSpill_CountTheirThreads(string fixture, string warningType, string expected)
    {
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze(fixture), warningType);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains(expected, StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.DoesNotContain("(s)", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ExchangeSpill_OneWrite_ReadsSingular()
    {
        // No checked-in plan spills a single write, so the fixture's ExchangeSpillDetails is
        // rewritten to one write before it is parsed; everything else in the plan is as recorded.
        var xml = Regex.Replace(ExchangeSpillPlanXml(), "(<ExchangeSpillDetails WritesToTempDb=\")\\d+(\")", "${1}1$2");
        var warnings = PlanTestHelper.WarningsOfType(ShowPlanParser.Parse(xml), "Exchange Spill");

        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.StartsWith("Exchange spill: 1 write to TempDB.", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ExchangeSpill_ManyWrites_ReadsPlural()
    {
        var warnings = PlanTestHelper.WarningsOfType(ShowPlanParser.Parse(ExchangeSpillPlanXml()), "Exchange Spill");

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.StartsWith("Exchange spill: 5,000,000 writes to TempDB.", StringComparison.Ordinal));
    }

    [Fact]
    public void ExchangeSpill_OnAGermanDesktop_WritesEveryNumberInOneConvention()
    {
        var warnings = OnAGermanDesktop(() => PlanTestHelper.WarningsOfType(ShowPlanParser.Parse(ExchangeSpillPlanXml()), "Exchange Spill"));

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.StartsWith("Exchange spill: 5,000,000 writes to TempDB.", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryStatus_OneOfEach_ReadsSingular()
    {
        var at = new DateTime(2026, 11, 9, 9, 0, 0);

        Assert.Equal(
            "1 interval, 1 plan, 1 total execution | 11/09 09:00 to 11/09 09:00",
            QueryStoreHistoryControl.FormatHistoryStatus(1, 1, 1, at, at));
    }

    [Fact]
    public void HistoryStatus_ManyOfEach_ReadsPlural()
    {
        var first = new DateTime(2026, 11, 9, 9, 0, 0);
        var last = new DateTime(2026, 11, 9, 11, 0, 0);

        Assert.Equal(
            "3 intervals, 2 plans, 1,250 total executions | 11/09 09:00 to 11/09 11:00",
            QueryStoreHistoryControl.FormatHistoryStatus(3, 2, 1250, first, last));
    }

    private static string ExchangeSpillPlanXml()
    {
        var path = Path.Combine("Plans", "exchange_spill_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    [Fact]
    public void IndexScript_OneIndex_ReadsSingular()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("PK_T", 1) });

        Assert.Contains("-- 1 index, 1 row", sql);
        Assert.DoesNotContain("(es)", sql);
    }

    [Fact]
    public void IndexScript_TwoIndexes_ReadsPlural()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("PK_T", 1000), Index("IX_T", 1000) });

        Assert.Contains("-- 2 indexes, 1,000 rows", sql);
        Assert.DoesNotContain("(es)", sql);
    }

    private static IndexInfo Index(string name, long rowCount) => new()
    {
        IndexName = name,
        IndexType = "NONCLUSTERED",
        IsUnique = false,
        IsPrimaryKey = false,
        KeyColumns = "Id",
        IncludeColumns = "",
        FilterDefinition = null,
        RowCount = rowCount,
        SizeMB = 1.5,
        DataCompression = "NONE"
    };

    // ---- The Key Lookup grade, the Lazy Spool message and the Wait Stats card ----------------

    [Fact]
    public void KeyLookup_OneLookupInARecordedPlan_ReadsSingular()
    {
        // The recorded plan's Key Lookup ran once.
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze("convert_implicit_plan.sqlplan"), "Key Lookup");

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains("Operator time: 0ms (0% of statement) over 1 lookup.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "over 1 lookup.")]
    [InlineData(12, "over 12 lookups.")]
    public void KeyLookup_CountsItsLookups(long executions, string expected)
    {
        var warning = AnalyzeSingle(Lookup(executions, ownMs: 100), statementMs: 1_000, "Key Lookup");

        Assert.Contains("Operator time: 100ms (10% of statement) " + expected, warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 500, 1, "500 rebinds (cache misses), 1 rewind (cache hit),")]
    [InlineData(true, 500, 20, "500 rebinds (cache misses), 20 rewinds (cache hits),")]
    [InlineData(true, 500, 0, "500 rebinds (cache misses), 0 rewinds (cache hits),")]
    // An estimated count is shown rounded, and the word follows the number shown: 1.3 and 1.49 read "1 rewind".
    [InlineData(false, 500, 1.3, "500 rebinds (cache misses), 1 rewind (cache hit),")]
    [InlineData(false, 500, 1.49, "500 rebinds (cache misses), 1 rewind (cache hit),")]
    // A midpoint rounds to the even neighbour, so 1.5 reads "2 rewinds", 2.5 "2 rewinds" and 0.5 "0 rewinds".
    [InlineData(false, 500, 1.5, "500 rebinds (cache misses), 2 rewinds (cache hits),")]
    [InlineData(false, 500, 2.5, "500 rebinds (cache misses), 2 rewinds (cache hits),")]
    [InlineData(false, 500, 0.5, "500 rebinds (cache misses), 0 rewinds (cache hits),")]
    public void LazySpool_CountsItsRewinds(bool actual, double rebinds, double rewinds, string expected)
    {
        var warning = AnalyzeSingle(LazySpool(actual, rebinds, rewinds), statementMs: 1_000, "Lazy Spool Ineffective");

        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitStatsCard_OneWaitTypeAndOneWait_ReadsSingular()
    {
        HeadlessUi.Run(() =>
        {
            // The recorded plan carries one wait type. Its WaitCount is rewritten to a single wait,
            // which no checked-in plan carries; everything else in the plan is as recorded.
            var xml = Regex.Replace(FixtureXml("mismatched_data_type_plan.sqlplan"), "WaitCount=\"\\d+\"", "WaitCount=\"1\"");
            var card = WaitStatsCard(xml);

            Assert.Equal("2 ms of waits across 1 wait type", card.HeaderTip);
            var row = Assert.Single(card.Rows);
            Assert.Equal("2 ms (1 wait)", row.Text);
            Assert.Equal("2 ms across 1 wait", row.Tip);
        });
    }

    [Fact]
    public void WaitStatsCard_ManyWaitTypesAndWaits_ReadsPlural()
    {
        HeadlessUi.Run(() =>
        {
            var card = WaitStatsCard(FixtureXml("isnull_plan.sqlplan"));

            Assert.Equal("Wait Stats — 1,022ms total", card.HeaderText);
            Assert.Equal("1,022 ms of waits across 3 wait types", card.HeaderTip);
            Assert.Contains(card.Rows, r => r.Text == "426 ms (1,409 waits)" && r.Tip == "426 ms across 1,409 waits");
        });
    }

    [Fact]
    public void LazySpool_OnAGermanDesktop_WritesEveryNumberInOneConvention()
    {
        var warning = OnAGermanDesktop(() => AnalyzeSingle(LazySpool(true, 1500, 2000), statementMs: 1_000, "Lazy Spool Ineffective"));

        Assert.Contains("1,500 rebinds (cache misses), 2,000 rewinds (cache hits), 1.3x rewinds (cache hits) per rebind (cache miss)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyLookup_OnAGermanDesktop_WritesEveryNumberInOneConvention()
    {
        // The lookup also filters 1,234 of its 5,000 rows, so the message carries a residual-predicate line too.
        var node = Lookup(5_000, ownMs: 1_200);
        node.Predicate = "[x]=(1)";
        node.ActualRows = 3_766;

        var warning = OnAGermanDesktop(() => AnalyzeSingle(node, statementMs: 10_000, "Key Lookup"));

        Assert.Contains("Residual predicate (filtered 1,234 rows): [x]=(1)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Operator time: 1,200ms (12% of statement) over 5,000 lookups.", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyLookup_NoStatementTime_OnAGermanDesktop_WritesItsCountInOneConvention()
    {
        // An actual plan older than SQL Server 2016 carries runtime counts but no statement time.
        var warning = OnAGermanDesktop(() => AnalyzeSingle(Lookup(5_000, ownMs: 0), statementMs: 0, "Key Lookup"));

        Assert.Contains("Lookups performed: 5,000. The plan recorded no statement time to grade them against.", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, 11, "Residual predicate (filtered 1 row): [x]=(1)")]
    [InlineData(5_000, 3_766, "Residual predicate (filtered 1,234 rows): [x]=(1)")]
    public void KeyLookup_ResidualPredicate_CountsItsFilteredRows(long executions, long rows, string expected)
    {
        var node = Lookup(executions, ownMs: 100);
        node.Predicate = "[x]=(1)";
        node.ActualRows = rows;

        var warning = AnalyzeSingle(node, statementMs: 1_000, "Key Lookup");

        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowEstimateMismatch_OneRowPerExecution_ReadsSingular()
    {
        // An inner-side join estimated at 10 rows per execution that returned one row on each of 500 executions.
        var inner = new PlanNode
        {
            NodeId = 2,
            PhysicalOp = "Nested Loops",
            LogicalOp = "Inner Join",
            HasActualStats = true,
            EstimateRows = 10,
            ActualRows = 500,
            ActualExecutions = 500
        };
        var join = NestedLoops(Scan(nodeId: 1, estimateRows: 500, actualRows: 500), inner);

        var stmt = new PlanStatement { RootNode = join, QueryTimeStats = new QueryTimeInfo { ElapsedTimeMs = 1_000 } };
        PlanAnalyzer.Analyze(new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] });
        var warning = Assert.Single(inner.Warnings, w => w.WarningType == "Row Estimate Mismatch");

        Assert.StartsWith("Estimated 10 vs Actual 500 (1 row x 500 executions): 10x overestimated.", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedLoops_OneRowOuterEstimate_ReadsSingular()
    {
        // An outer side estimated at one row that produced 100,001, driving the inner side that many times.
        var inner = new PlanNode { NodeId = 2, PhysicalOp = "Index Seek", LogicalOp = "Index Seek", HasActualStats = true, EstimateRows = 1, ActualRows = 100_001, ActualExecutions = 100_001 };
        var join = NestedLoops(Scan(nodeId: 1, estimateRows: 1, actualRows: 100_001), inner);

        var stmt = new PlanStatement { RootNode = join, QueryTimeStats = new QueryTimeInfo { ElapsedTimeMs = 1_000 } };
        PlanAnalyzer.Analyze(new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] });
        var warning = Assert.Single(join.Warnings, w => w.WarningType == "Nested Loops High Executions");

        Assert.Contains("Outer side: estimated 1 row, actual 100,001 (100001x underestimate).", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyToManyMerge_OneLogicalRead_ReadsSingular()
    {
        var merge = new PlanNode
        {
            NodeId = 0,
            PhysicalOp = "Merge Join",
            LogicalOp = "Inner Join",
            ManyToMany = true,
            HasActualStats = true,
            ActualLogicalReads = 1
        };

        var warning = AnalyzeSingle(merge, statementMs: 1_000, "Many-to-Many Merge Join");

        Assert.Contains("worktable in TempDB (1 logical read)", warning.Message, StringComparison.Ordinal);
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

    [Fact]
    public void WaitStatsCard_OnAGermanDesktop_WritesEveryNumberInOneConvention()
    {
        HeadlessUi.Run(() =>
        {
            var card = OnAGermanDesktop(() => WaitStatsCard(FixtureXml("isnull_plan.sqlplan")));

            Assert.Equal("Wait Stats — 1,022ms total", card.HeaderText);
            Assert.Equal("1,022 ms of waits across 3 wait types", card.HeaderTip);
            Assert.Contains(card.Rows, r => r.Text == "426 ms (1,409 waits)" && r.Tip == "426 ms across 1,409 waits");
        });
    }

    private static PlanWarning AnalyzeSingle(PlanNode root, long statementMs, string warningType)
    {
        var stmt = new PlanStatement { RootNode = root, QueryTimeStats = new QueryTimeInfo { ElapsedTimeMs = statementMs } };
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
        PlanAnalyzer.Analyze(plan);
        return Assert.Single(root.Warnings, w => w.WarningType == warningType);
    }

    private static PlanNode Lookup(long executions, long ownMs) => new()
    {
        NodeId = 0,
        PhysicalOp = "Key Lookup (Clustered)",
        LogicalOp = "Key Lookup",
        Lookup = true,
        HasActualStats = true,
        ActualExecutions = executions,
        ActualRows = executions,
        ActualElapsedMs = ownMs
    };

    /// <summary>An actual plan's spool carries whole rebind and rewind counts; an estimated plan's carries fractions.</summary>
    private static PlanNode LazySpool(bool actual, double rebinds, double rewinds) => new()
    {
        NodeId = 0,
        PhysicalOp = "Table Spool",
        LogicalOp = "Lazy Spool",
        HasActualStats = actual,
        ActualRebinds = actual ? (long)rebinds : 0,
        ActualRewinds = actual ? (long)rewinds : 0,
        ActualExecutions = actual ? (long)(rebinds + rewinds) : 0,
        ActualRows = actual ? (long)(rebinds + rewinds) : 0,
        EstimateRebinds = actual ? 0 : rebinds,
        EstimateRewinds = actual ? 0 : rewinds
    };

    /// <summary>
    /// Loads a plan into a shown viewer and reads the Wait Stats card: the header's tooltip and each
    /// row's duration text with its tooltip.
    /// </summary>
    private static (string? HeaderTip, string HeaderText, List<(string Text, string? Tip)> Rows) WaitStatsCard(string xml)
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "waits.sqlplan"), viewer.LastLoadError);
        var window = new Window { Content = viewer, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var header = viewer.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "WaitStatsHeader");
        var content = viewer.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "WaitStatsContent");
        var rows = content.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => ToolTip.GetTip(t) is string tip && tip.Contains(" ms across ", StringComparison.Ordinal))
            .Select(t => (t.Text ?? string.Empty, ToolTip.GetTip(t) as string))
            .ToList();
        return (ToolTip.GetTip(header) as string, header.Text ?? string.Empty, rows);
    }

    /// <summary>
    /// Runs <paramref name="read"/> with the thread's culture set to German, which groups with a dot
    /// and uses a decimal comma, and restores the culture afterwards. The culture is per thread, so
    /// a test running alongside is not touched.
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
        var path = Path.Combine("Plans", fixture);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }
}
