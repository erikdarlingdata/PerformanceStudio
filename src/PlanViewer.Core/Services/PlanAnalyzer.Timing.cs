using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static void FindMemoryConsumers(PlanNode node, List<string> consumers)
    {
        // Collect all consumers first, then sort by row count descending
        var raw = new List<(string Label, double Rows)>();
        FindMemoryConsumersRecursive(node, raw);

        foreach (var (label, _) in raw.OrderByDescending(c => c.Rows))
            consumers.Add(label);
    }

    private static void FindMemoryConsumersRecursive(PlanNode node, List<(string Label, double Rows)> consumers)
    {
        if (node.PhysicalOp.Contains("Sort", StringComparison.OrdinalIgnoreCase) &&
            !node.PhysicalOp.Contains("Spool", StringComparison.OrdinalIgnoreCase))
        {
            var rowCount = node.HasActualStats ? node.ActualRows : node.EstimateRows;
            var rows = node.HasActualStats
                ? $"{node.ActualRows:N0} actual rows"
                : $"{node.EstimateRows:N0} estimated rows";
            consumers.Add(($"Sort (Node {node.NodeId}, {rows})", rowCount));
        }
        else if (node.PhysicalOp.Contains("Hash", StringComparison.OrdinalIgnoreCase))
        {
            var rowCount = node.HasActualStats ? node.ActualRows : node.EstimateRows;
            var rows = node.HasActualStats
                ? $"{node.ActualRows:N0} actual rows"
                : $"{node.EstimateRows:N0} estimated rows";
            consumers.Add(($"Hash Match (Node {node.NodeId}, {rows})", rowCount));
        }

        foreach (var child in node.Children)
            FindMemoryConsumersRecursive(child, consumers);
    }

    /// <summary>
    /// Calculates an operator's own elapsed time by subtracting child time.
    /// In batch mode, operator times are self-contained (exclusive).
    /// In row mode, times are cumulative (include all children below).
    /// For parallel plans, we calculate self-time per-thread then take the max,
    /// avoiding cross-thread subtraction errors.
    /// Exchange operators accumulate downstream wait time (e.g. from spilling
    /// children) so their self-time is unreliable — see sql.kiwi/2021/03.
    /// </summary>
    internal static long GetOperatorOwnElapsedMs(PlanNode node)
    {
        if (node.ActualExecutionMode == "Batch")
            return node.ActualElapsedMs;

        // Parallel plan with per-thread data: calculate self-time per thread
        if (node.PerThreadStats.Count > 1)
            return GetPerThreadOwnElapsed(node);

        // Serial row mode: subtract all direct children's elapsed time
        return GetSerialOwnElapsed(node);
    }

    /// <summary>
    /// Threads that actually did work. In a parallel plan thread 0 is the
    /// coordinator: it carries no rows, and its ActualElapsedms is the wall clock
    /// of the whole parallel branch. Including it in a per-thread self-time
    /// calculation hands the operator the branch's entire duration.
    /// A serial plan has a single thread numbered 0, which IS a worker, so only
    /// exclude thread 0 when other threads exist.
    /// </summary>
    private static List<PerThreadRuntimeInfo> WorkThreads(PlanNode node)
    {
        var workers = node.PerThreadStats.Where(t => t.ThreadId > 0).ToList();
        return workers.Count > 0 ? workers : node.PerThreadStats;
    }

    /// <summary>
    /// Per-thread self-time calculation for parallel row mode operators.
    /// For each worker thread: self = parent[t] - sum(effective children[t]).
    /// Returns max across worker threads.
    /// </summary>
    private static long GetPerThreadOwnElapsed(PlanNode node) =>
        GetPerThreadOwn(node, t => t.ActualElapsedMs, n => n.ActualElapsedMs);

    private static long GetPerThreadOwn(
        PlanNode node,
        Func<PerThreadRuntimeInfo, long> pick,
        Func<PlanNode, long> nodeTotal)
    {
        var parentByThread = new Dictionary<int, long>();
        foreach (var ts in WorkThreads(node))
            parentByThread[ts.ThreadId] = pick(ts);

        var childSumByThread = new Dictionary<int, long>();
        foreach (var child in node.Children)
            AddEffectiveChildByThread(child, pick, nodeTotal, childSumByThread);

        var maxSelf = 0L;
        foreach (var (threadId, parentMs) in parentByThread)
        {
            childSumByThread.TryGetValue(threadId, out var childMs);
            var self = Math.Max(0, parentMs - childMs);
            if (self > maxSelf) maxSelf = self;
        }

        return maxSelf;
    }

    /// <summary>
    /// What a child contributes to its parent's per-thread total. The per-thread
    /// mirror of GetEffectiveChildElapsedMs, and it must look through the same two
    /// things the serial path does, or the parent absorbs the subtree beneath them:
    ///
    ///   - A batch-mode child reports STANDALONE time, so only its own value would
    ///     come off and the rest of the batch zone would stay in the parent.
    ///   - A pass-through child (Compute Scalar) carries no runtime stats at all,
    ///     so zero would come off.
    ///
    /// Together these crowned a row-mode Nested Loops above a batch Hash Aggregate
    /// as the hottest operator in its plan, with the aggregate's time counted twice.
    /// </summary>
    private static void AddEffectiveChildByThread(
        PlanNode child,
        Func<PerThreadRuntimeInfo, long> pick,
        Func<PlanNode, long> nodeTotal,
        Dictionary<int, long> acc)
    {
        // Exchange operators have unreliable times — look through to their child.
        if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
        {
            var dominant = child.Children.OrderByDescending(nodeTotal).First();
            AddEffectiveChildByThread(dominant, pick, nodeTotal, acc);
            return;
        }

        var mode = child.ActualExecutionMode ?? child.ExecutionMode;
        if (mode == "Batch" && child.HasActualStats)
        {
            AddBatchSubtreeByThread(child, pick, nodeTotal, acc);
            return;
        }

        if (child.HasActualStats && nodeTotal(child) > 0)
        {
            foreach (var ts in WorkThreads(child))
            {
                acc.TryGetValue(ts.ThreadId, out var existing);
                acc[ts.ThreadId] = existing + pick(ts);
            }
            return;
        }

        // No runtime stats: look through to the descendants that have them.
        foreach (var grandchild in child.Children)
            AddEffectiveChildByThread(grandchild, pick, nodeTotal, acc);
    }

    /// <summary>
    /// Per-thread sum across a contiguous batch-mode zone, stopping at exchanges.
    /// Batch operators pipeline, so their times add rather than nest.
    /// </summary>
    private static void AddBatchSubtreeByThread(
        PlanNode node,
        Func<PerThreadRuntimeInfo, long> pick,
        Func<PlanNode, long> nodeTotal,
        Dictionary<int, long> acc)
    {
        foreach (var ts in WorkThreads(node))
        {
            acc.TryGetValue(ts.ThreadId, out var existing);
            acc[ts.ThreadId] = existing + pick(ts);
        }

        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism") continue; // zone boundary

            var childMode = child.ActualExecutionMode ?? child.ExecutionMode;
            if (childMode == "Batch" && child.HasActualStats)
                AddBatchSubtreeByThread(child, pick, nodeTotal, acc);
            else
                AddEffectiveChildByThread(child, pick, nodeTotal, acc);
        }
    }

    /// <summary>
    /// Max per-thread self-CPU for this operator.
    /// Parallel: for each thread, self_cpu = thread_cpu - Σ same-thread child cpu; take max.
    /// Serial / single-thread: operator_cpu - Σ effective child cpu.
    /// Needed for external-wait benefit scoring (Joe's formula).
    /// </summary>
    internal static long GetOperatorMaxThreadOwnCpuMs(PlanNode node)
    {
        if (!node.HasActualStats || node.ActualCPUMs <= 0) return 0;

        if (node.PerThreadStats.Count > 1)
            return GetPerThreadOwn(node, t => t.ActualCPUMs, n => n.ActualCPUMs);

        // Serial: operator_cpu - Σ effective child cpu
        var totalChildCpu = 0L;
        foreach (var child in node.Children)
            totalChildCpu += GetEffectiveChildCpuMs(child);
        return Math.Max(0, node.ActualCPUMs - totalChildCpu);
    }

    private static long GetEffectiveChildCpuMs(PlanNode child)
    {
        if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            return child.Children.Max(GetEffectiveChildCpuMs);
        if (child.ActualCPUMs > 0)
            return child.ActualCPUMs;
        if (child.Children.Count == 0)
            return 0;
        var sum = 0L;
        foreach (var grandchild in child.Children)
            sum += GetEffectiveChildCpuMs(grandchild);
        return sum;
    }

    /// <summary>
    /// Serial row mode self-time: subtract all direct children's effective elapsed.
    /// Pass-through operators (Compute Scalar, etc.) don't carry runtime stats —
    /// look through them to the first descendant that does. Exchange children
    /// use max-child elapsed because exchange times are unreliable.
    /// </summary>
    private static long GetSerialOwnElapsed(PlanNode node)
    {
        var totalChildElapsed = 0L;
        foreach (var child in node.Children)
            totalChildElapsed += GetEffectiveChildElapsedMs(child);

        return Math.Max(0, node.ActualElapsedMs - totalChildElapsed);
    }

    /// <summary>
    /// Returns the elapsed time a child contributes to its parent's subtree.
    /// Looks through pass-through operators (Compute Scalar, Parallelism exchange)
    /// that don't carry reliable runtime stats.
    /// </summary>
    private static long GetEffectiveChildElapsedMs(PlanNode child)
    {
        // Exchange operators: unreliable times, use max child
        if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            return child.Children.Max(GetEffectiveChildElapsedMs);

        // Batch mode pipelines — each operator's elapsed stands alone rather than
        // rolling up its descendants the way row-mode does. For a parent computing
        // self-time above a batch-mode subtree, subtract the whole pipeline's time
        // (Joe #215 D1: Parallelism gather-streams above three batch operators).
        var mode = child.ActualExecutionMode ?? child.ExecutionMode;
        if (mode == "Batch" && child.HasActualStats)
            return SumBatchSubtreeElapsedMs(child);

        // Child has its own stats: use them
        if (child.ActualElapsedMs > 0)
            return child.ActualElapsedMs;

        // No stats (Compute Scalar and similar): look through to descendants
        if (child.Children.Count == 0)
            return 0;

        var sum = 0L;
        foreach (var grandchild in child.Children)
            sum += GetEffectiveChildElapsedMs(grandchild);
        return sum;
    }

    /// <summary>
    /// Sums ActualElapsedMs across a contiguous batch-mode subtree (stops at
    /// Parallelism exchange zone boundaries). Batch operators pipeline — elapsed
    /// times are standalone, not cumulative — so summing gives the total work the
    /// zone did, which is what a row-mode parent above the zone should subtract
    /// to get its own self-time.
    /// </summary>
    private static long SumBatchSubtreeElapsedMs(PlanNode node)
    {
        long sum = node.ActualElapsedMs;
        foreach (var child in node.Children)
        {
            // Zone boundary — stop summing
            if (child.PhysicalOp == "Parallelism") continue;

            var childMode = child.ActualExecutionMode ?? child.ExecutionMode;
            if (childMode == "Batch" && child.HasActualStats)
                sum += SumBatchSubtreeElapsedMs(child);
            else
                sum += GetEffectiveChildElapsedMs(child);
        }
        return sum;
    }

    /// <summary>
    /// Calculates a Parallelism (exchange) operator's own elapsed time.
    /// Exchange times are unreliable — they accumulate wait time caused by
    /// downstream operators (e.g. spilling sorts). This returns a best-effort
    /// value but callers should treat it with caution.
    /// </summary>
    private static long GetParallelismOperatorElapsedMs(PlanNode node)
    {
        if (node.Children.Count == 0)
            return node.ActualElapsedMs;

        if (node.PerThreadStats.Count > 1)
            return GetPerThreadOwnElapsed(node);

        var maxChildElapsed = node.Children.Max(c => c.ActualElapsedMs);
        return Math.Max(0, node.ActualElapsedMs - maxChildElapsed);
    }

    /// <summary>
    /// Quantifies the cost of work below a Filter operator by summing child subtree metrics.
    /// Shows how many rows, reads, and elapsed time were spent producing rows that the
    /// Filter then discarded.
    /// </summary>
    private static string QuantifyFilterImpact(PlanNode filterNode)
    {
        if (filterNode.Children.Count == 0)
            return "";

        var parts = new List<string>();

        // Rows input vs output — how many rows did the filter discard?
        var inputRows = filterNode.Children.Sum(c => c.ActualRows);
        if (filterNode.HasActualStats && inputRows > 0 && filterNode.ActualRows < inputRows)
        {
            var discarded = inputRows - filterNode.ActualRows;
            var pct = (double)discarded / inputRows * 100;
            parts.Add($"{discarded:N0} of {inputRows:N0} rows discarded ({pct:N0}%)");
        }

        // Logical reads across the entire child subtree
        long totalReads = 0;
        foreach (var child in filterNode.Children)
            totalReads += SumSubtreeReads(child);
        if (totalReads > 0)
            parts.Add($"{totalReads:N0} logical reads below");

        // Elapsed time: use the direct child's time (cumulative in row mode, includes its children)
        var childElapsed = filterNode.Children.Max(c => c.ActualElapsedMs);
        if (childElapsed > 0)
            parts.Add($"{childElapsed:N0}ms elapsed below");

        if (parts.Count == 0)
            return "";

        return string.Join("\n", parts.Select(p => "• " + p));
    }

    /// <summary>
    /// Detects well-known CE default selectivity guesses by comparing EstimateRows to TableCardinality.
    /// Returns a description of the guess pattern, or null if no known pattern matches.
    /// </summary>
    private static string? DetectCeGuess(double estimateRows, double tableCardinality)
    {
        if (tableCardinality <= 0) return null;
        var selectivity = estimateRows / tableCardinality;

        // Known CE guess selectivities with a 2% tolerance band
        return selectivity switch
        {
            >= 0.29 and <= 0.31 => $"matches the 30% equality guess ({selectivity * 100:N1}%)",
            >= 0.098 and <= 0.102 => $"matches the 10% inequality guess ({selectivity * 100:N1}%)",
            >= 0.088 and <= 0.092 => $"matches the 9% LIKE/BETWEEN guess ({selectivity * 100:N1}%)",
            >= 0.155 and <= 0.175 => $"matches the ~16.4% compound predicate guess ({selectivity * 100:N1}%)",
            >= 0.009 and <= 0.011 => $"matches the 1% multi-inequality guess ({selectivity * 100:N1}%)",
            _ => null
        };
    }

    private static long SumSubtreeReads(PlanNode node)
    {
        long reads = node.ActualLogicalReads;
        foreach (var child in node.Children)
            reads += SumSubtreeReads(child);
        return reads;
    }

    /// <summary>
    /// Builds impact details for a scan node: what % of plan time/cost it represents,
    /// and what fraction of rows survived filtering.
    /// </summary>
    private static ScanImpact BuildScanImpactDetails(PlanNode node, PlanStatement stmt)
    {
        var parts = new List<string>();

        // % of plan cost
        double costPct = 0;
        if (stmt.StatementSubTreeCost > 0 && node.EstimatedTotalSubtreeCost > 0)
        {
            costPct = node.EstimatedTotalSubtreeCost / stmt.StatementSubTreeCost * 100;
            if (costPct >= 50)
                parts.Add($"This scan is {costPct:N0}% of the plan cost.");
        }

        // % of elapsed time (actual plans)
        double elapsedPct = 0;
        if (node.HasActualStats && node.ActualElapsedMs > 0 &&
            stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs > 0)
        {
            elapsedPct = (double)node.ActualElapsedMs / stmt.QueryTimeStats.ElapsedTimeMs * 100;
            if (elapsedPct >= 50)
                parts.Add($"This scan took {elapsedPct:N0}% of elapsed time.");
        }

        // Row selectivity: rows returned vs rows read (actual) or vs table cardinality (estimated)
        if (node.HasActualStats && node.ActualRowsRead > 0 && node.ActualRows < node.ActualRowsRead)
        {
            var selectivity = (double)node.ActualRows / node.ActualRowsRead * 100;
            if (selectivity < 10)
                parts.Add($"Only {selectivity:N3}% of rows survived filtering ({node.ActualRows:N0} of {node.ActualRowsRead:N0}).");
        }
        else if (!node.HasActualStats && node.TableCardinality > 0 && node.EstimateRows < node.TableCardinality)
        {
            var selectivity = node.EstimateRows / node.TableCardinality * 100;
            if (selectivity < 10)
                parts.Add($"Only {selectivity:N1}% of rows estimated to survive filtering.");
        }

        return new ScanImpact(costPct, elapsedPct, parts.Count > 0 ? string.Join(" ", parts) : null);
    }
}
