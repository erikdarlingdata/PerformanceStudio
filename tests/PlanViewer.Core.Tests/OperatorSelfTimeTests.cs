using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
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

    [Fact]
    public void SerialRowModeParentAboveBatchZoneDoesNotAbsorbItsCpu()
    {
        // A serial row-mode Stream Aggregate (node 4) sits above a batch Sort (node 5)
        // over a row-mode Nested Loops (node 6). Batch CPU is standalone, so the zone
        // under the aggregate used 14,142 + 11,659 ms. Subtracting only the Sort's
        // 14,142 left 14,771 ms of "self" CPU beside 3,059 ms of self elapsed.
        var root = RootOf("param-sniffing-posttypeid2.sqlplan");
        var aggregate = TryFindNode(root, 4)!;

        Assert.InRange(PlanAnalyzer.GetOperatorOwnElapsedMs(aggregate), 3_000, 3_100);
        Assert.InRange(PlanAnalyzer.GetOperatorOwnCpuMs(aggregate), 3_050, 3_200);
        Assert.InRange(PlanAnalyzer.GetOperatorMaxThreadOwnCpuMs(aggregate), 3_050, 3_200);
        Assert.InRange(NodeTimeAttribution.GetOwnCpuMs(aggregate), 3_050, 3_200);
    }

    [Fact]
    public void ParallelOwnCpuSumsThreadsAndMaxThreadKeepsTheBusiestOne()
    {
        // A DOP 8 Clustered Index Scan (node 1) burned 624 ms across its workers in a
        // 629 ms query. Its own CPU is the sum. The busiest thread alone had 79 ms,
        // which is what the external-wait formula wants from the max-thread figure.
        var root = RootOf("case_predicate_plan.sqlplan");
        var scan = TryFindNode(root, 1)!;

        Assert.InRange(PlanAnalyzer.GetOperatorOwnCpuMs(scan), 600, 640);
        Assert.InRange(PlanAnalyzer.GetOperatorMaxThreadOwnCpuMs(scan), 70, 90);
    }

    [Fact]
    public void BatchOperatorMaxThreadCpuIsNotReducedByItsChildren()
    {
        // Batch mode reports each thread's own CPU. The max-thread figure used to
        // subtract the children's CPU from it, though the batch number never held it.
        var root = RootOf("parallel_row_over_batch_plan.sqlplan");
        var hashMatch = TryFindNode(root, 2)!;
        var busiest = hashMatch.PerThreadStats.Where(t => t.ThreadId > 0).Max(t => t.ActualCPUMs);

        Assert.Equal(busiest, PlanAnalyzer.GetOperatorMaxThreadOwnCpuMs(hashMatch));
    }

    [Fact]
    public void ExpensiveOperatorsTextUsesTheAnalyzersOwnTimes()
    {
        // The CLI's "Expensive operators" list did its own row-mode subtraction and
        // charged the Stream Aggregate with 14,771 ms CPU and 14,720 ms elapsed. Its
        // CPU shares added up to 159%.
        var plan = PlanTestHelper.LoadAndAnalyze("param-sniffing-posttypeid2.sqlplan");
        var text = TextFormatter.Format(ResultMapper.Map(plan, "param-sniffing-posttypeid2.sqlplan"));
        var lines = text.Split('\n');
        var at = System.Array.FindIndex(lines, l => l.Contains("Stream Aggregate (Node 4)"));

        Assert.True(at >= 0, "node 4 is no longer listed as an expensive operator");
        Assert.Contains("3,112ms CPU", lines[at + 1]);
        Assert.Contains("3,059ms elapsed", lines[at + 1]);
    }

    // ---- #680: self time across an exchange ------------------------------------------------------
    //
    // A parallel row-mode operator's own time is its time on each worker thread minus its children's
    // time on the same thread, maxed over threads. An exchange moves rows between threads, so thread t
    // above it is not doing thread t's work below it. Every consumer thread waits on all the producers
    // and none finishes before the slowest does, so the slowest producer thread below the exchange
    // comes off every thread above it: the per-thread form of the serial path's max child.

    /// <summary>
    /// The issue's own case. The Sort (node 2) is 17,111 ms on threads 1 to 8. Below its Repartition
    /// Streams the Nested Loops (node 4) is 17,110 ms on thread 1 and 0 ms on threads 2 to 8. The
    /// slowest producer thread comes off every Sort thread: 17,111 - 17,110 = 1 ms. Pairing by thread
    /// number gave threads 2 to 8 the whole 17,111 ms as the Sort's own.
    /// </summary>
    [Fact]
    public void SortAboveASkewedRepartition_OwnsOneMillisecond()
    {
        var sort = TryFindNode(RootOf("serially-parallel.sqlplan"), 2)!;
        Assert.Equal("Sort", sort.PhysicalOp);
        Assert.True(sort.PerThreadStats.Count > 1, "the Sort must take the per-thread path for this test to mean anything");

        Assert.Equal(1, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    /// <summary>
    /// The exchange's own figure, which an exchange spill warning quotes as its operator time, has
    /// the same shape: its consumer threads are 17,111 ms each and its producer is the same Nested
    /// Loops, 17,110 ms on thread 1 only. 17,111 - 17,110 = 1 ms, not 17,111.
    /// </summary>
    [Fact]
    public void RepartitionOverASkewedBranch_OwnsOneMillisecond()
    {
        var exchange = TryFindNode(RootOf("serially-parallel.sqlplan"), 3)!;
        Assert.Equal("Parallelism", exchange.PhysicalOp);

        Assert.Equal(1, PlanAnalyzer.GetOperatorOwnElapsedMs(exchange));
    }

    /// <summary>
    /// The spilling child through an exchange keeps its spill. In spill_plan the Sort (node 11)
    /// spills and runs 25,746 to 26,232 ms per thread. The Repartition Streams below it (node 12)
    /// reports 12,621 to 13,406 ms per thread, about half of it time spent waiting on the spilling
    /// Sort rather than work the scan below did. The scan (node 13) runs 1,044 to 1,294 ms per thread.
    /// The slowest scan thread off every Sort thread: 26,232 - 1,294 = 24,938 ms. Taking the exchange's
    /// own time off the same thread instead would leave the Sort 26,232 - 12,621 = 13,611 ms and hand
    /// the rest of its spill to the exchange. Pairing the scan by thread number gave 26,232 - 1,084 =
    /// 25,148 ms.
    /// </summary>
    [Fact]
    public void SpillingSortAboveARepartition_KeepsItsSpillTime()
    {
        var sort = TryFindNode(RootOf("spill_plan.sqlplan"), 11)!;
        Assert.Equal("Sort", sort.PhysicalOp);
        Assert.Equal("Parallelism", Assert.Single(sort.Children).PhysicalOp);

        Assert.Equal(24_938, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    private static PerThreadRuntimeInfo Thread(int id, long elapsedMs, long cpuMs = 0) =>
        new() { ThreadId = id, ActualElapsedMs = elapsedMs, ActualCPUMs = cpuMs };

    private static PlanNode Producer(PerThreadRuntimeInfo[] threads) => new()
    {
        PhysicalOp = "Nested Loops",
        HasActualStats = true,
        ActualElapsedMs = threads.Max(t => t.ActualElapsedMs),
        ActualCPUMs = threads.Sum(t => t.ActualCPUMs),
        PerThreadStats = [.. threads],
    };

    private static PlanNode Repartition(PerThreadRuntimeInfo[] threads, PlanNode child) => new()
    {
        PhysicalOp = "Parallelism",
        LogicalOp = "Repartition Streams",
        HasActualStats = true,
        ActualElapsedMs = threads.Max(t => t.ActualElapsedMs),
        PerThreadStats = [.. threads],
        Children = [child],
    };

    private static PlanNode SortOver(PerThreadRuntimeInfo[] threads, PlanNode child) => new()
    {
        PhysicalOp = "Sort",
        HasActualStats = true,
        ActualElapsedMs = threads.Max(t => t.ActualElapsedMs),
        ActualCPUMs = threads.Sum(t => t.ActualCPUMs),
        PerThreadStats = [.. threads],
        Children = [child],
    };

    /// <summary>
    /// The issue's shape in miniature: the Sort is 100 ms on both threads, the producer below the
    /// exchange did all its work on thread 1 (99 ms). Own time = 100 - 99 = 1 ms. Pairing by thread
    /// number gave 100 ms (thread 2: 100 - 0).
    /// </summary>
    [Fact]
    public void SkewedProducer_IsNotPairedByThreadNumber()
    {
        PerThreadRuntimeInfo[] sortThreads = [Thread(0, 0), Thread(1, 100), Thread(2, 100)];
        var sort = SortOver(sortThreads, Repartition(sortThreads, Producer([Thread(0, 0), Thread(1, 99), Thread(2, 0)])));

        Assert.Equal(1, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    /// <summary>
    /// No skew: the producer is 80 ms on both threads, the Sort is 100 and 95 ms. Pairing by thread
    /// gives max(100 - 80, 95 - 80) = 20 ms, and so does taking the slowest producer thread off each
    /// Sort thread. The fix leaves this result alone.
    /// </summary>
    [Fact]
    public void EvenProducer_SelfTimeIsUnchanged()
    {
        PerThreadRuntimeInfo[] sortThreads = [Thread(0, 0), Thread(1, 100), Thread(2, 95)];
        var sort = SortOver(sortThreads, Repartition(sortThreads, Producer([Thread(0, 0), Thread(1, 80), Thread(2, 80)])));

        Assert.Equal(20, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    /// <summary>
    /// A pass-through (Compute Scalar, no runtime stats) between two exchanges. The Sort is 100 ms
    /// on both threads. The slowest producer under the inner exchange is thread 1 of the Nested
    /// Loops, 90 ms, and it comes off every Sort thread: 100 - 90 = 10 ms. The every-thread time must
    /// ride up through the pass-through, or the Sort keeps all 100 ms.
    /// </summary>
    [Fact]
    public void ExchangeBehindAPassThrough_TakesItsSlowestProducerOffEveryThread()
    {
        PerThreadRuntimeInfo[] sortThreads = [Thread(0, 0), Thread(1, 100), Thread(2, 100)];
        var inner = Repartition(sortThreads, Producer([Thread(0, 0), Thread(1, 90), Thread(2, 0)]));
        var passThrough = new PlanNode { PhysicalOp = "Compute Scalar", HasActualStats = false, Children = [inner] };
        var sort = SortOver(sortThreads, Repartition(sortThreads, passThrough));

        Assert.Equal(10, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    /// <summary>
    /// A batch-mode zone between two exchanges. The row-mode Sort is 100 ms on both threads. Below the
    /// outer exchange the batch Hash Match is 30 ms on each thread (a batch operator's time is its own)
    /// and, through a row-mode Compute Scalar with no runtime stats, an inner exchange's slowest
    /// producer is 50 ms. The slowest thread below the outer exchange is 30 + 50 = 80 ms, so the Sort
    /// owns 100 - 80 = 20 ms. Losing the zone's every-thread time would give it 70 ms.
    /// </summary>
    [Fact]
    public void ExchangeUnderABatchZone_TakesItsSlowestProducerOffEveryThread()
    {
        PerThreadRuntimeInfo[] sortThreads = [Thread(0, 0), Thread(1, 100), Thread(2, 100)];
        var inner = Repartition(sortThreads, Producer([Thread(0, 0), Thread(1, 50), Thread(2, 0)]));
        var passThrough = new PlanNode
        {
            PhysicalOp = "Compute Scalar",
            ExecutionMode = "Row",
            ActualExecutionMode = "Row",
            HasActualStats = false,
            Children = [inner],
        };
        var hashMatch = new PlanNode
        {
            PhysicalOp = "Hash Match",
            ExecutionMode = "Batch",
            ActualExecutionMode = "Batch",
            HasActualStats = true,
            ActualElapsedMs = 30,
            PerThreadStats = [Thread(0, 0), Thread(1, 30), Thread(2, 30)],
            Children = [passThrough],
        };
        var sort = SortOver(sortThreads, Repartition(sortThreads, hashMatch));
        sort.ExecutionMode = "Row";
        sort.ActualExecutionMode = "Row";

        Assert.Equal(20, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
    }

    /// <summary>
    /// CPU does not take the every-thread subtraction. A consumer thread never burns a producer
    /// thread's CPU, so taking the slowest producer's CPU off every consumer thread would subtract CPU
    /// the thread never held and zero the Sort here (10 - 99 on both threads). Own CPU keeps the
    /// same-thread pairing: thread 1 is 10 - 99, clamped to 0, thread 2 is 10 - 0 = 10. The busiest
    /// thread is 10 ms and the sum across threads is 10 ms, as before.
    /// </summary>
    [Fact]
    public void SkewedProducer_CpuKeepsTheSameThreadPairing()
    {
        PerThreadRuntimeInfo[] sortThreads = [Thread(0, 0, 0), Thread(1, 100, 10), Thread(2, 100, 10)];
        var producer = Producer([Thread(0, 0, 0), Thread(1, 99, 99), Thread(2, 0, 0)]);
        var sort = SortOver(sortThreads, Repartition(sortThreads, producer));

        Assert.Equal(1, PlanAnalyzer.GetOperatorOwnElapsedMs(sort));
        Assert.Equal(10, PlanAnalyzer.GetOperatorMaxThreadOwnCpuMs(sort));
        Assert.Equal(10, PlanAnalyzer.GetOperatorOwnCpuMs(sort));
    }
}
