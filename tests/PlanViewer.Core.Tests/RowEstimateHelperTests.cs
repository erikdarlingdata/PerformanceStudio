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
}
