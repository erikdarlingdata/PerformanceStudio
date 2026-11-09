using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The scan rules' gates (#679). Rule 34 "Bare Scan" and rule 11 "Scan With Predicate" report a scan
/// only past 10,000 rows read and a tenth of the statement's own time; rule 11 also needs the
/// predicate to discard more than half of the rows read, takes its Critical level from the measured
/// share in an actual plan, and names the rows read and returned. Rule 24 "Top Above Scan" leaves a
/// plain Top over an Ordered scan alone. The parser keeps EstimatedRowsRead at 0 when the plan does
/// not carry it. One actual-plan statement per case, built from the shape SQL Server 2022 writes.
/// </summary>
public class ScanRuleGateTests
{
    private const string ActualScanTemplate = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.4135.4"><BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT a FROM dbo.t{WHERE}" StatementId="1" StatementCompId="1" StatementType="SELECT" StatementSubTreeCost="{COST}" StatementEstRows="{ROWS}" StatementOptmLevel="FULL" CardinalityEstimationModelVersion="160"><QueryPlan DegreeOfParallelism="1" CachedPlanSize="24" CompileTime="0" CompileCPU="0" CompileMemory="144">{TIME}<RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="{ROWS}" EstimatedRowsRead="{TABLE}" EstimateIO="0.6" EstimateCPU="0.1" AvgRowSize="15" EstimatedTotalSubtreeCost="{COST}" TableCardinality="{TABLE}" Parallel="0" EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row"><OutputList><ColumnReference Database="[g]" Schema="[dbo]" Table="[t]" Column="a" /></OutputList>{RUNTIME}<IndexScan Ordered="0" ForcedIndex="0" ForceSeek="0" ForceScan="0" NoExpandHint="0" Storage="RowStore"><DefinedValues><DefinedValue><ColumnReference Database="[g]" Schema="[dbo]" Table="[t]" Column="a" /></DefinedValue></DefinedValues><Object Database="[g]" Schema="[dbo]" Table="[t]" Index="[PK_t]" IndexKind="Clustered" Storage="RowStore" />{PREDICATE}</IndexScan></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    /// <summary>
    /// An actual plan of one scan. <paramref name="statementMs"/> of 0 leaves the statement time out of
    /// the plan, which is what an older actual plan looks like.
    /// </summary>
    private static ParsedPlan ActualScan(long rowsRead, long rowsReturned, long scanMs, long statementMs,
        bool withPredicate, double costShareOfStatement = 1.0)
    {
        var xml = ActualScanTemplate
            .Replace("{WHERE}", withPredicate ? " WHERE b = 1" : "")
            .Replace("{COST}", "10")
            .Replace("{ROWS}", rowsReturned.ToString())
            .Replace("{TABLE}", rowsRead.ToString())
            .Replace("{TIME}", statementMs > 0 ? $"<QueryTimeStats CpuTime=\"{statementMs}\" ElapsedTime=\"{statementMs}\" />" : "")
            .Replace("{RUNTIME}", $"<RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"{rowsReturned}\" ActualRowsRead=\"{rowsRead}\" Batches=\"0\" ActualEndOfScans=\"1\" ActualExecutions=\"1\" ActualExecutionMode=\"Row\" ActualElapsedms=\"{scanMs}\" ActualCPUms=\"{scanMs}\" ActualScans=\"1\" ActualLogicalReads=\"100\" ActualPhysicalReads=\"0\" ActualReadAheads=\"0\" ActualLobLogicalReads=\"0\" ActualLobPhysicalReads=\"0\" ActualLobReadAheads=\"0\" /></RunTimeInformation>")
            .Replace("{PREDICATE}", withPredicate ? "<Predicate><ScalarOperator ScalarString=\"[g].[dbo].[t].[b]=(1)\" /></Predicate>" : "");

        var plan = ShowPlanParser.Parse(xml);
        // The scan's cost share of the statement: the statement's cost is 10 in the template.
        ScanNode(plan).EstimatedTotalSubtreeCost = 10 * costShareOfStatement;
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    private static PlanNode ScanNode(ParsedPlan plan)
    {
        var scan = PlanTestHelper.FindNode(PlanTestHelper.FirstStatement(plan).RootNode!, 0);
        Assert.NotNull(scan);
        return scan;
    }

    private static ParsedPlan EstimatedScan(long tableRows, bool withPredicate)
    {
        var xml = ActualScanTemplate
            .Replace("{WHERE}", withPredicate ? " WHERE b = 1" : "")
            .Replace("{COST}", "10")
            .Replace("{ROWS}", (tableRows / 10).ToString())
            .Replace("{TABLE}", tableRows.ToString())
            .Replace("{TIME}", "")
            .Replace("{RUNTIME}", "")
            .Replace("{PREDICATE}", withPredicate ? "<Predicate><ScalarOperator ScalarString=\"[g].[dbo].[t].[b]=(1)\" /></Predicate>" : "");

        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    private static PlanWarning? Finding(ParsedPlan plan, string warningType) =>
        PlanTestHelper.WarningsOfType(plan, warningType).SingleOrDefault();

    // ---- rule 34: Bare Scan ----------------------------------------------------------------------

    [Fact]
    public void BareScan_TwoHundredRows_SixtyPercentOfStatement_DoesNotWarn()
    {
        // Most of a 10 ms statement, and still a 200 row table: nothing a narrower index could spare.
        var plan = ActualScan(rowsRead: 200, rowsReturned: 200, scanMs: 6, statementMs: 10, withPredicate: false);

        Assert.Null(Finding(plan, "Bare Scan"));
    }

    [Fact]
    public void BareScan_MillionRows_OnePercentOfStatement_DoesNotWarn()
    {
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 1_000_000, scanMs: 10, statementMs: 1000, withPredicate: false);

        Assert.Null(Finding(plan, "Bare Scan"));
    }

    [Fact]
    public void BareScan_MillionRows_SixtyPercentOfStatement_Warns()
    {
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 1_000_000, scanMs: 600, statementMs: 1000, withPredicate: false);

        Assert.NotNull(Finding(plan, "Bare Scan"));
    }

    [Fact]
    public void BareScan_MillionRows_NoStatementTime_AnyOwnTimeWarns()
    {
        // An actual plan without QueryTimeStats has no share to measure, so any own time stands in.
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 1_000_000, scanMs: 1, statementMs: 0, withPredicate: false);

        Assert.NotNull(Finding(plan, "Bare Scan"));
    }

    [Fact]
    public void BareScan_Estimated_TwoHundredRowTable_FullCost_DoesNotWarn()
    {
        Assert.Null(Finding(EstimatedScan(tableRows: 200, withPredicate: false), "Bare Scan"));
    }

    [Fact]
    public void BareScan_Estimated_MillionRowTable_FullCost_Warns()
    {
        Assert.NotNull(Finding(EstimatedScan(tableRows: 1_000_000, withPredicate: false), "Bare Scan"));
    }

    // ---- rule 11: Scan With Predicate ------------------------------------------------------------

    [Fact]
    public void ScanWithPredicate_ReturnsHalfOfMillionRead_DoesNotWarn()
    {
        // Half of the rows read came back, so a seek on the predicate columns would touch most of
        // the same pages.
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 500_000, scanMs: 600, statementMs: 1000, withPredicate: true);

        Assert.Null(Finding(plan, "Scan With Predicate"));
    }

    [Fact]
    public void ScanWithPredicate_LowSelectivity_OnePercentOfStatement_DoesNotWarn()
    {
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 100, scanMs: 10, statementMs: 1000, withPredicate: true);

        Assert.Null(Finding(plan, "Scan With Predicate"));
    }

    [Fact]
    public void ScanWithPredicate_LowSelectivity_HighCostShare_LowTimeShare_IsWarning_NamesRows()
    {
        // 95% of the estimated cost, 15% of the measured time: the measured share is the one that
        // sets the level in an actual plan, so this is a Warning, and the message gives the rows.
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 100, scanMs: 150, statementMs: 1000,
            withPredicate: true, costShareOfStatement: 0.95);
        var finding = Finding(plan, "Scan With Predicate");

        Assert.NotNull(finding);
        Assert.Equal(PlanWarningSeverity.Warning, finding.Severity);
        Assert.Contains("read 1,000,000 rows to return 100", finding.Message);
        Assert.DoesNotContain("reading every row", finding.Message);
    }

    [Fact]
    public void ScanWithPredicate_LowSelectivity_NinetyFivePercentOfStatement_IsCritical()
    {
        var plan = ActualScan(rowsRead: 1_000_000, rowsReturned: 100, scanMs: 950, statementMs: 1000,
            withPredicate: true, costShareOfStatement: 0.15);
        var finding = Finding(plan, "Scan With Predicate");

        Assert.NotNull(finding);
        Assert.Equal(PlanWarningSeverity.Critical, finding.Severity);
        Assert.Contains("read 1,000,000 rows to return 100", finding.Message);
    }

    [Fact]
    public void ScanWithPredicate_Estimated_NinetyFivePercentOfCost_IsCritical()
    {
        // An estimated plan has only the cost share, and is reported as before.
        var xml = ActualScanTemplate
            .Replace("{WHERE}", " WHERE b = 1").Replace("{COST}", "10").Replace("{ROWS}", "100")
            .Replace("{TABLE}", "1000000").Replace("{TIME}", "").Replace("{RUNTIME}", "")
            .Replace("{PREDICATE}", "<Predicate><ScalarOperator ScalarString=\"[g].[dbo].[t].[b]=(1)\" /></Predicate>");
        var plan = ShowPlanParser.Parse(xml);
        ScanNode(plan).EstimatedTotalSubtreeCost = 9.5;
        PlanAnalyzer.Analyze(plan);
        var finding = Finding(plan, "Scan With Predicate");

        Assert.NotNull(finding);
        Assert.Equal(PlanWarningSeverity.Critical, finding.Severity);
        Assert.Contains("reading every row", finding.Message);
    }

    // ---- rule 24: Top Above Scan -----------------------------------------------------------------

    private static PlanNode TopOver(PlanNode scan, bool topNSort)
    {
        var top = topNSort
            ? new PlanNode { NodeId = 0, PhysicalOp = "Sort", LogicalOp = "Top N Sort" }
            : new PlanNode { NodeId = 0, PhysicalOp = "Top", LogicalOp = "Top" };
        top.Children.Add(scan);
        scan.Parent = top;
        return top;
    }

    private static PlanNode Scan(bool ordered) => new()
    {
        NodeId = 1, PhysicalOp = "Clustered Index Scan", LogicalOp = "Clustered Index Scan",
        ObjectName = "dbo.t", Ordered = ordered
    };

    private static bool TopAboveScanFires(PlanNode root)
    {
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [new PlanStatement { RootNode = root }] }] };
        PlanAnalyzer.Analyze(plan);
        return PlanTestHelper.WarningsOfType(plan, "Top Above Scan").Count > 0;
    }

    [Fact]
    public void TopAboveScan_PlainTop_OverOrderedScan_DoesNotWarn() =>
        Assert.False(TopAboveScanFires(TopOver(Scan(ordered: true), topNSort: false)));

    [Fact]
    public void TopAboveScan_PlainTop_OverOrderedScan_ThroughComputeScalar_DoesNotWarn()
    {
        var scan = Scan(ordered: true);
        var compute = new PlanNode { NodeId = 2, PhysicalOp = "Compute Scalar", LogicalOp = "Compute Scalar" };
        compute.Children.Add(scan);
        scan.Parent = compute;

        Assert.False(TopAboveScanFires(TopOver(compute, topNSort: false)));
    }

    [Fact]
    public void TopAboveScan_PlainTop_OverUnorderedScan_Warns() =>
        Assert.True(TopAboveScanFires(TopOver(Scan(ordered: false), topNSort: false)));

    [Fact]
    public void TopAboveScan_TopNSort_OverOrderedScan_StillWarns() =>
        // A Top N Sort reads the whole scan before it can return a row, ordered or not.
        Assert.True(TopAboveScanFires(TopOver(Scan(ordered: true), topNSort: true)));

    [Fact]
    public void TopAboveScan_ApplyTop_OverOrderedScan_OnNestedLoopsInner_DoesNotWarn()
    {
        // CROSS APPLY (SELECT TOP (1) ... ORDER BY <index key>): the Top sits on the inner side of a
        // Nested Loops over an Ordered scan. The Critical inner-side finding is for an unordered scan.
        var outer = new PlanNode { NodeId = 3, PhysicalOp = "Clustered Index Scan", LogicalOp = "Clustered Index Scan", ObjectName = "dbo.o" };
        var top = TopOver(Scan(ordered: true), topNSort: false);
        var loops = new PlanNode { NodeId = 4, PhysicalOp = "Nested Loops", LogicalOp = "Inner Join" };
        loops.Children.Add(outer);
        loops.Children.Add(top);
        outer.Parent = loops;
        top.Parent = loops;

        Assert.False(TopAboveScanFires(loops));
    }

    // ---- the parser's EstimatedRowsRead ----------------------------------------------------------

    [Fact]
    public void Parser_AbsentEstimatedRowsRead_StaysZero_BesideTheNoRowGoalEstimate()
    {
        // top_above_scan_plan's Nested Loops (Node 1) carries EstimateRowsWithoutRowGoal and no
        // EstimatedRowsRead. They are different quantities, so the one the plan left out stays 0.
        var plan = PlanTestHelper.LoadAndAnalyze("top_above_scan_plan.sqlplan");
        var loops = PlanTestHelper.FindNode(PlanTestHelper.FirstStatement(plan).RootNode!, 1);

        Assert.NotNull(loops);
        Assert.Equal(16_067_900, loops.EstimateRowsWithoutRowGoal);
        Assert.Equal(0, loops.EstimatedRowsRead);
    }

    [Fact]
    public void Parser_PresentEstimatedRowsRead_IsKept()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("top_above_scan_plan.sqlplan");
        var scan = PlanTestHelper.FindNode(PlanTestHelper.FirstStatement(plan).RootNode!, 2);

        Assert.NotNull(scan);
        Assert.Equal(17_142_200, scan.EstimatedRowsRead);
    }
}
