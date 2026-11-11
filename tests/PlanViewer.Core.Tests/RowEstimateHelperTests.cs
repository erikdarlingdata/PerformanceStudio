using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #594: EstimateRows is always per execution. ActualRows is the total across every execution —
/// or, in a parallel zone, across every thread. RowEstimateHelper decides when ActualExecutions
/// is a real per-execution count (the inner side of a Nested Loops join) versus a parallel
/// zone's thread count (everywhere else). eager_index_spool_plan.sqlplan is the fixture the
/// coordinator worked the numbers from: DOP 8, every RelOp Parallel="true".
/// </summary>
public class RowEstimateHelperTests
{
    // ---------------------------------------------------------------
    // IsInnerSideOfNestedLoops — hand-built trees, full control over Parent/Children wiring
    // ---------------------------------------------------------------

    [Fact]
    public void IsInnerSide_NoNestedLoopsAncestor_IsFalse()
    {
        var node = new PlanNode { PhysicalOp = "Clustered Index Scan" };

        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(node));
    }

    /// <summary>
    /// The OUTER (first) child of a Nested Loops join is not inner-side — it runs once, not once
    /// per outer row.
    /// </summary>
    [Fact]
    public void IsInnerSide_OuterChildOfNestedLoops_IsFalse()
    {
        var outer = new PlanNode { PhysicalOp = "Sort" };
        var inner = new PlanNode { PhysicalOp = "Index Seek" };
        var nl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outer, inner } };
        outer.Parent = nl;
        inner.Parent = nl;

        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(outer));
    }

    /// <summary>
    /// The INNER (second) child of a Nested Loops join is inner-side — the direct case #594 is
    /// about.
    /// </summary>
    [Fact]
    public void IsInnerSide_InnerChildOfNestedLoops_IsTrue()
    {
        var outer = new PlanNode { PhysicalOp = "Sort" };
        var inner = new PlanNode { PhysicalOp = "Index Seek" };
        var nl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outer, inner } };
        outer.Parent = nl;
        inner.Parent = nl;

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(inner));
    }

    /// <summary>
    /// A node several levels below the inner child (e.g. a spool feeding a scan) is still
    /// inner-side — the walk climbs through every ancestor, not just the immediate parent.
    /// </summary>
    [Fact]
    public void IsInnerSide_DescendantOfInnerChild_IsTrue()
    {
        var outer = new PlanNode { PhysicalOp = "Sort" };
        var spool = new PlanNode { PhysicalOp = "Index Spool" };
        var scan = new PlanNode { PhysicalOp = "Clustered Index Scan" };
        var nl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outer, spool } };
        outer.Parent = nl;
        spool.Parent = nl;
        spool.Children.Add(scan);
        scan.Parent = spool;

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(scan));
    }

    /// <summary>
    /// Nested loops within loops: a node under the inner side of an inner Nested Loops, which is
    /// itself the outer side of an outer Nested Loops, is still inner-side because of the inner
    /// join — being on the outer side of the outer join does not cancel that out.
    /// </summary>
    [Fact]
    public void IsInnerSide_NestedLoopsWithinLoops_WalksThroughEveryAncestor()
    {
        var deepInner = new PlanNode { PhysicalOp = "Key Lookup" };
        var innerNlOuter = new PlanNode { PhysicalOp = "Index Seek" };
        var innerNl = new PlanNode { PhysicalOp = "Nested Loops", Children = { innerNlOuter, deepInner } };
        innerNlOuter.Parent = innerNl;
        deepInner.Parent = innerNl;

        var outerNlOuter = new PlanNode { PhysicalOp = "Clustered Index Scan" };
        var outerNl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outerNlOuter, innerNl } };
        outerNlOuter.Parent = outerNl;
        innerNl.Parent = outerNl;

        // innerNl is the outer join's inner (second) child, so everything under it is inner-side —
        // including deepInner, which is also the inner join's own inner child.
        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(innerNl));
        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(deepInner));
        // outerNlOuter is the outer join's outer child and has no other Nested Loops ancestor.
        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(outerNlOuter));
    }

    // ---------------------------------------------------------------
    // GetExpectedRows / GetRowAccuracyRatio
    // ---------------------------------------------------------------

    [Fact]
    public void GetExpectedRows_NonInnerSide_IsNotMultipliedByExecutions()
    {
        // A parallel zone's ActualExecutions counts threads, not repeats, for a node that isn't
        // itself repeated by a loop.
        var node = new PlanNode { PhysicalOp = "Nested Loops", EstimateRows = 2983.02, ActualExecutions = 8 };

        Assert.Equal(2983.02, RowEstimateHelper.GetExpectedRows(node), 6);
    }

    [Fact]
    public void GetExpectedRows_InnerSide_IsMultipliedByExecutions()
    {
        var outer = new PlanNode { PhysicalOp = "Sort" };
        var inner = new PlanNode { PhysicalOp = "Top", EstimateRows = 1, ActualExecutions = 613 };
        var nl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outer, inner } };
        outer.Parent = nl;
        inner.Parent = nl;

        Assert.Equal(613, RowEstimateHelper.GetExpectedRows(inner), 6);
    }

    /// <summary>
    /// #594's core guard: an inner-side node that never executed does not get its (irrelevant)
    /// estimate multiplied by zero.
    /// </summary>
    [Fact]
    public void GetExpectedRows_InnerSideButNeverExecuted_FallsBackToRawEstimate()
    {
        var outer = new PlanNode { PhysicalOp = "Sort" };
        var inner = new PlanNode { PhysicalOp = "Index Seek", EstimateRows = 5, ActualExecutions = 0 };
        var nl = new PlanNode { PhysicalOp = "Nested Loops", Children = { outer, inner } };
        outer.Parent = nl;
        inner.Parent = nl;

        Assert.Equal(5, RowEstimateHelper.GetExpectedRows(inner), 6);
    }

    [Fact]
    public void GetRowAccuracyRatio_ZeroExpectedAndZeroActual_IsNeutral()
    {
        var node = new PlanNode { EstimateRows = 0, ActualRows = 0 };

        Assert.Equal(1.0, RowEstimateHelper.GetRowAccuracyRatio(node));
    }

    [Fact]
    public void GetRowAccuracyRatio_ZeroExpectedButSomeActual_IsUnbounded()
    {
        var node = new PlanNode { EstimateRows = 0, ActualRows = 100 };

        Assert.Equal(double.MaxValue, RowEstimateHelper.GetRowAccuracyRatio(node));
    }

    // ---------------------------------------------------------------
    // eager_index_spool_plan.sqlplan — the coordinator's worked numbers (#594)
    // ---------------------------------------------------------------

    private static PlanNode Node(string planFile, int nodeId)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(planFile);
        var root = plan.Batches[0].Statements[0].RootNode!;
        var node = PlanTestHelper.FindNode(root, nodeId);
        Assert.NotNull(node);
        return node!;
    }

    /// <summary>
    /// Node 1, the outer Nested Loops: not inner-side. 609 actual over an EstimateRows of
    /// 2983.02 is a 4.9x overestimate (20%) — not the 39x a thread-summed ActualExecutions of 8
    /// would give if it were multiplied in.
    /// </summary>
    [Fact]
    public void EagerIndexSpoolPlan_Node1_NotInnerSide_RatioIsFourPointNineX()
    {
        var node1 = Node("eager_index_spool_plan.sqlplan", 1);

        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(node1));
        Assert.Equal(8, node1.ActualExecutions);
        Assert.Equal(609, node1.ActualRows);
        Assert.Equal(2983.02, RowEstimateHelper.GetExpectedRows(node1), 2);

        var ratio = RowEstimateHelper.GetRowAccuracyRatio(node1);
        Assert.Equal(0.204, ratio, 3);
        Assert.Equal(4.9, 1.0 / ratio, 1);
    }

    /// <summary>
    /// Nodes 4 and 5, Top and Index Spool: both inner-side of Node 1. 609 actual over 613
    /// executions against an EstimateRows of 1 is 99% — the estimate was right.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void EagerIndexSpoolPlan_InnerSideNodes_RatioIsNinetyNinePercent(int nodeId)
    {
        var node = Node("eager_index_spool_plan.sqlplan", nodeId);

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(node));
        Assert.Equal(613, node.ActualExecutions);
        Assert.Equal(609, node.ActualRows);
        Assert.Equal(613, RowEstimateHelper.GetExpectedRows(node), 6);

        var ratio = RowEstimateHelper.GetRowAccuracyRatio(node);
        Assert.Equal(0.993, ratio, 3);
    }

    /// <summary>
    /// Node 6, the Clustered Index Scan under the spool: also inner-side, but executed once, so
    /// multiplying by ActualExecutions is a no-op and the expected rows equal the raw estimate.
    /// </summary>
    [Fact]
    public void EagerIndexSpoolPlan_Node6_InnerSideButSingleExecution_MatchesRawEstimate()
    {
        var node6 = Node("eager_index_spool_plan.sqlplan", 6);

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(node6));
        Assert.Equal(1, node6.ActualExecutions);
        Assert.Equal(8_042_005, node6.ActualRows);
        Assert.Equal(8_042_010, RowEstimateHelper.GetExpectedRows(node6), 6);
    }

    /// <summary>
    /// The HTML export (the web viewer's download) writes the same "N of M rows" line as the
    /// node label, so it takes the same estimate: the join reads against its own estimate, the
    /// inner-side spool against its estimate times its executions.
    /// </summary>
    [Fact]
    public void EagerIndexSpoolPlan_HtmlExport_UsesTheExecutionAwareEstimate()
    {
        const string plan = "eager_index_spool_plan.sqlplan";
        var result = PlanViewer.Core.Output.ResultMapper.Map(PlanTestHelper.LoadAndAnalyze(plan), plan);

        var html = PlanViewer.Core.Output.HtmlExporter.Export(
            result, PlanViewer.Core.Output.TextFormatter.Format(result));

        Assert.Contains("609 of 2,983 rows (20%)", html);
        Assert.Contains("609 of 613 rows (99%)", html);
        Assert.DoesNotContain("609 of 1 rows", html);
    }

    // ---------------------------------------------------------------
    // GetEstimatedExecutions / GetEstimatedRowsAllExecutions (#677)
    // ---------------------------------------------------------------

    /// <summary>
    /// The optimizer expects an operator to run once, plus once per rebind and once per rewind. An
    /// inner-side spool with 10 rebinds and 990 rewinds runs 1,001 times, and at 10 rows a run
    /// returns 10,010 rows in all; counting only the rebinds (the old Properties-panel math) would
    /// have said 11 runs and 110 rows.
    /// </summary>
    [Fact]
    public void GetEstimatedExecutions_CountsOnePlusRebindsPlusRewinds()
    {
        var spool = new PlanNode { PhysicalOp = "Table Spool", EstimateRows = 10, EstimateRebinds = 10, EstimateRewinds = 990 };

        Assert.Equal(1001, RowEstimateHelper.GetEstimatedExecutions(spool), 6);
        Assert.Equal(10_010, RowEstimateHelper.GetEstimatedRowsAllExecutions(spool), 6);
    }

    [Fact]
    public void GetEstimatedExecutions_NoRebindsOrRewinds_IsOne()
    {
        var scan = new PlanNode { PhysicalOp = "Clustered Index Scan", EstimateRows = 2983.02 };

        Assert.Equal(1, RowEstimateHelper.GetEstimatedExecutions(scan), 6);
        Assert.Equal(2983.02, RowEstimateHelper.GetEstimatedRowsAllExecutions(scan), 6);
    }

    /// <summary>
    /// cte_multi_ref_plan's Node 3, a lazy Table Spool with no rebinds and 99 rewinds: 100 runs of
    /// 100 rows, 10,000 rows in all — not the single run the rebinds alone would count.
    /// </summary>
    [Fact]
    public void CteMultiRefPlan_Node3_LazySpool_RunsOncePerRewind()
    {
        var spool = Node("cte_multi_ref_plan.sqlplan", 3);

        Assert.Equal(0, spool.EstimateRebinds);
        Assert.Equal(99, spool.EstimateRewinds);
        Assert.Equal(100, RowEstimateHelper.GetEstimatedExecutions(spool), 6);
        Assert.Equal(10_000, RowEstimateHelper.GetEstimatedRowsAllExecutions(spool), 6);
    }

    /// <summary>
    /// lazy_spool_plan's Node 2, an Index Spool with fractional rebinds (6,021,690) and rewinds
    /// (1,133.87): the count keeps the fraction, since the estimates are fractional too.
    /// </summary>
    [Fact]
    public void LazySpoolPlan_Node2_IndexSpool_KeepsTheFractionalCount()
    {
        var spool = Node("lazy_spool_plan.sqlplan", 2);

        Assert.Equal(6_022_824.87, RowEstimateHelper.GetEstimatedExecutions(spool), 2);
    }

    // ---------------------------------------------------------------
    // Per-thread executions: what one thread's ActualExecutions adds up to
    // ---------------------------------------------------------------

    private static PlanNode Threaded(string physicalOp, double estimateRows, params (int Thread, long Rows, long Executions)[] threads)
    {
        var node = new PlanNode { PhysicalOp = physicalOp, EstimateRows = estimateRows, HasActualStats = true };
        foreach (var (thread, rows, executions) in threads)
        {
            node.PerThreadStats.Add(new PerThreadRuntimeInfo { ThreadId = thread, ActualRows = rows, ActualExecutions = executions });
            node.ActualRows += rows;
            node.ActualExecutions += executions;
        }
        return node;
    }

    private static void Wire(PlanNode parent, params PlanNode[] children)
    {
        foreach (var child in children)
        {
            parent.Children.Add(child);
            child.Parent = parent;
        }
    }

    /// <summary>
    /// A serial Nested Loops whose inner side is a Gather Streams over a parallel scan re-runs the
    /// whole parallel branch per outer row. Three outer rows: each of four workers opens the scan
    /// three times, so the threads sum to twelve, but the scan ran three times, each run split
    /// between the threads. Multiplying the estimate by twelve made a scan that returned exactly
    /// its estimate on every run read as a 4x (DOP) overestimate.
    /// </summary>
    [Fact]
    public void GetExpectedRows_ParallelBranchReboundUnderASerialLoop_CountsEachRunOnce()
    {
        var outer = Threaded("Constant Scan", 3, (0, 3, 1));
        var gather = Threaded("Parallelism", 100, (0, 300, 3));
        var scan = Threaded("Clustered Index Scan", 100, (1, 75, 3), (2, 75, 3), (3, 75, 3), (4, 75, 3));
        var nl = Threaded("Nested Loops", 300, (0, 300, 1));
        Wire(nl, outer, gather);
        Wire(gather, scan);

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(scan));
        Assert.Equal(12, scan.ActualExecutions);
        Assert.Equal(3, RowEstimateHelper.GetActualExecutions(scan));
        Assert.Equal(300, RowEstimateHelper.GetExpectedRows(scan), 6);
        Assert.Equal(1.0, RowEstimateHelper.GetRowAccuracyRatio(scan), 6);

        // The Gather Streams itself runs on the loop's thread: three executions, as reported.
        Assert.Equal(3, RowEstimateHelper.GetActualExecutions(gather));
        Assert.Equal(300, RowEstimateHelper.GetExpectedRows(gather), 6);
    }

    /// <summary>
    /// The guard on the other side: within a parallel Nested Loops' inner side every thread runs
    /// whole executions for the outer rows it holds, so the threads' executions do add up. Taking
    /// the busiest thread's count would invent an underestimate. The figures are the inner-side
    /// seek of a real DOP 8 plan (5,829 executions, 5,718 rows, estimate 1 per execution).
    /// </summary>
    [Fact]
    public void GetActualExecutions_ParallelInnerSide_SumsAcrossThreads()
    {
        long[] executions = { 587, 1017, 561, 455, 438, 672, 1348, 751 };
        long[] rows = { 573, 1001, 546, 439, 429, 659, 1336, 735 };
        var outer = Threaded("Clustered Index Scan", 27572.3,
            executions.Select((e, i) => (i + 1, e, 1L)).ToArray());
        var seek = Threaded("Index Seek", 1,
            executions.Select((e, i) => (i + 1, rows[i], e)).ToArray());
        var nl = Threaded("Nested Loops", 27572.3,
            executions.Select((e, i) => (i + 1, e, 1L)).ToArray());
        Wire(nl, outer, seek);

        Assert.Equal(5829, RowEstimateHelper.GetActualExecutions(seek));
        Assert.Equal(5829, RowEstimateHelper.GetExpectedRows(seek), 6);
        Assert.Equal(0.981, RowEstimateHelper.GetRowAccuracyRatio(seek), 3);

        // The outer scan ran once, its pages split between eight threads: 5,829 of 27,572.
        Assert.Equal(1, RowEstimateHelper.GetActualExecutions(outer));
        Assert.Equal(27572.3, RowEstimateHelper.GetExpectedRows(outer), 6);
    }

    /// <summary>
    /// An Adaptive Join's inputs are the build, the hash probe and the nested-loops inner side.
    /// When it runs as a nested loop the third input runs once per outer row exactly like a
    /// Nested Loops inner side, and its estimate is per execution.
    /// </summary>
    [Fact]
    public void IsInnerSide_ThirdInputOfAdaptiveJoin_IsTrue()
    {
        var build = new PlanNode { PhysicalOp = "Clustered Index Scan" };
        var probe = new PlanNode { PhysicalOp = "Clustered Index Scan" };
        var seek = new PlanNode { PhysicalOp = "Clustered Index Seek" };
        var join = new PlanNode { PhysicalOp = "Adaptive Join", IsAdaptive = true };
        Wire(join, build, probe, seek);

        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(seek));
        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(build));
        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(probe));
    }

    /// <summary>
    /// adaptive_join_nested_loops_plan.sqlplan, captured on SQL Server 2025 at DOP 8: Badges joined
    /// to Users, threshold 11,904 rows, 3,264 arrived, so the Adaptive Join (Node 3) ran as a
    /// nested loop. The Users seek (Node 10) ran 3,264 times, one row each, against an estimate of
    /// one row per execution. It read as "3,264 of 1 rows" before the third input counted as an
    /// inner side.
    /// </summary>
    [Fact]
    public void AdaptiveJoinNestedLoopsPlan_Node10_InnerSeek_RatioIsOne()
    {
        const string plan = "adaptive_join_nested_loops_plan.sqlplan";
        var join = Node(plan, 3);
        var seek = Node(plan, 10);

        Assert.True(join.IsAdaptive);
        Assert.Equal(10, join.Children[2].NodeId);
        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(seek));
        Assert.Equal(3264, seek.ActualExecutions);
        Assert.Equal(3264, seek.ActualRows);
        Assert.Equal(3264, RowEstimateHelper.GetExpectedRows(seek), 6);
        Assert.Equal(1.0, RowEstimateHelper.GetRowAccuracyRatio(seek), 6);

        var result = PlanViewer.Core.Output.ResultMapper.Map(PlanTestHelper.LoadAndAnalyze(plan), plan);
        var html = PlanViewer.Core.Output.HtmlExporter.Export(
            result, PlanViewer.Core.Output.TextFormatter.Format(result));
        Assert.DoesNotContain("3,264 of 1 rows", html);
        Assert.Contains("3,264 of 3,264 rows (100%)", html);
    }

    /// <summary>
    /// The same plan's Badges scan (Node 5) and the join above it are not inner-side: one
    /// execution, its pages split between eight threads, 3,264 rows against 13,666 estimated.
    /// </summary>
    [Fact]
    public void AdaptiveJoinNestedLoopsPlan_Node5_ParallelScan_IsOneExecution()
    {
        var scan = Node("adaptive_join_nested_loops_plan.sqlplan", 5);

        Assert.False(RowEstimateHelper.IsInnerSideOfNestedLoops(scan));
        Assert.Equal(8, scan.ActualExecutions);
        Assert.Equal(1, RowEstimateHelper.GetActualExecutions(scan));
        Assert.Equal(13666, RowEstimateHelper.GetExpectedRows(scan), 6);
    }
}
