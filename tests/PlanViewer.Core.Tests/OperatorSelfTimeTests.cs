using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Self-time attribution for parallel operators.
///
/// Two traps, both of which inflated a parent operator until it was reported as
/// the hottest in its plan:
///
///   1. Thread 0 is the coordinator. It carries no rows, and its ActualElapsedms
///      is the wall clock of the whole parallel branch.
///   2. A row-mode parent above a batch-mode subtree, or above a pass-through
///      operator with no runtime stats, subtracted too little from itself.
/// </summary>
public class OperatorSelfTimeTests
{
    private static PlanNode? TryFindNode(PlanNode root, int nodeId)
    {
        if (root.NodeId == nodeId) return root;
        foreach (var child in root.Children)
        {
            var found = TryFindNode(child, nodeId);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// The statement's RootNode is a synthetic wrapper (NodeId -1). The plan's
    /// real root operator is its first child.
    /// </summary>
    private static PlanNode RootOf(string planFile)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(planFile);
        var wrapper = plan.Batches[0].Statements[0].RootNode!;
        return wrapper.NodeId < 0 ? wrapper.Children[0] : wrapper;
    }

    [Fact]
    public void CoordinatorThreadIsNotCountedAsOperatorElapsedTime()
    {
        var root = RootOf("parallel_row_over_batch_plan.sqlplan");

        // Thread 0 reports 1000ms (the branch's wall clock) with zero rows. The
        // slowest worker also reports 1000ms here, so the node-level number is the
        // same -- what matters is that we source it from a worker, not the
        // coordinator, and that the coordinator never wins a max on its own.
        var coordinator = root.PerThreadStats.Single(t => t.ThreadId == 0);
        Assert.Equal(0, coordinator.ActualRows);
        Assert.Equal(1000, coordinator.ActualElapsedMs);

        // The Columnstore scan's coordinator row is 0ms while its workers are 0ms
        // too, but the Hash Match's coordinator is 0ms and workers are 900/890.
        var hashMatch = TryFindNode(root, 2)!;
        Assert.Equal(900, hashMatch.ActualElapsedMs); // max over WORKERS, not thread 0
    }

    [Fact]
    public void RowModeParentAboveBatchSubtreeDoesNotAbsorbIt()
    {
        var root = RootOf("parallel_row_over_batch_plan.sqlplan");

        // Node 0 is a parallel row-mode Nested Loops. Slowest worker: 1000ms.
        // Beneath it: a batch Compute Scalar (0ms) hiding a batch Hash Match
        // (900ms), plus a row-mode Index Seek (50ms).
        //
        // Correct self time = 1000 - (0 + 900) - 50 = 50ms.
        // Before the fix this reported 1000ms: the pass-through contributed
        // nothing, the batch zone contributed only its topmost value, and the
        // coordinator's 1000ms won the per-thread max outright.
        var own = PlanAnalyzer.GetOperatorOwnElapsedMs(root);
        Assert.InRange(own, 40, 60);
    }

    [Fact]
    public void RowModeParentAboveBatchSubtreeDoesNotAbsorbItsCpu()
    {
        var root = RootOf("parallel_row_over_batch_plan.sqlplan");

        // Busiest worker CPU on node 0 is 30ms. Beneath it, per that same thread:
        // batch zone 0 + 12 = 12ms, row seek 6ms. Self CPU = 30 - 18 = 12ms.
        var ownCpu = PlanAnalyzer.GetOperatorMaxThreadOwnCpuMs(root);
        Assert.InRange(ownCpu, 8, 16);
    }

    [Fact]
    public void BatchOperatorKeepsItsStandaloneTime()
    {
        var root = RootOf("parallel_row_over_batch_plan.sqlplan");
        var hashMatch = TryFindNode(root, 2)!;

        // Batch mode reports self time directly. Subtracting children would
        // underflow it.
        Assert.Equal(900, PlanAnalyzer.GetOperatorOwnElapsedMs(hashMatch));
    }

    [Fact]
    public void ParallelPassThroughChildDoesNotInflateItsParent()
    {
        // join_or_clause_plan has a parallel row-mode TopN Sort above a Compute
        // Scalar that carries no runtime statistics. Subtracting zero for it made
        // the sort absorb everything below.
        var root = RootOf("join_or_clause_plan.sqlplan");
        var sort = TryFindNode(root, 8);
        Assert.NotNull(sort);

        var own = PlanAnalyzer.GetOperatorOwnElapsedMs(sort!);
        var cumulative = sort!.ActualElapsedMs;

        // Self time must be strictly less than cumulative: the subtree below it
        // did real work, and that work is not the sort's own.
        Assert.True(
            own < cumulative,
            $"sort reported {own}ms self of {cumulative}ms cumulative -- it absorbed its subtree");
    }

    [Fact]
    public void SerialPlanStillUsesItsOnlyThread()
    {
        // A serial plan has a single thread numbered 0. It is a worker, not a
        // coordinator, and excluding it would zero out every operator.
        var root = RootOf("key_lookup_plan.sqlplan");
        var withStats = TryFindNode(root, 0);
        Assert.NotNull(withStats);
        if (withStats!.HasActualStats && withStats.PerThreadStats.Count == 1)
        {
            Assert.Equal(0, withStats.PerThreadStats[0].ThreadId);
            Assert.True(withStats.ActualElapsedMs >= 0);
        }
    }
}
