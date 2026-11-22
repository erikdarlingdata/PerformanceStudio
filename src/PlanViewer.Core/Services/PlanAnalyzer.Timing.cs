using System;
using System.Collections.Generic;
using System.Globalization;
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
                ? string.Create(CultureInfo.InvariantCulture, $"{node.ActualRows:N0} actual rows")
                : string.Create(CultureInfo.InvariantCulture, $"{node.EstimateRows:N0} estimated rows");
            consumers.Add(($"Sort (Node {node.NodeId}, {rows})", rowCount));
        }
        else if (node.PhysicalOp.Contains("Hash", StringComparison.OrdinalIgnoreCase))
        {
            var rowCount = node.HasActualStats ? node.ActualRows : node.EstimateRows;
            var rows = node.HasActualStats
                ? string.Create(CultureInfo.InvariantCulture, $"{node.ActualRows:N0} actual rows")
                : string.Create(CultureInfo.InvariantCulture, $"{node.EstimateRows:N0} estimated rows");
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
        if ((node.ActualExecutionMode ?? node.ExecutionMode) == "Batch")
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
    /// Which per-thread counter the own-time helpers attribute, and how a child on the
    /// producer side of an exchange comes off the parent's threads.
    ///
    /// Elapsed takes the slowest producer thread below the exchange off every thread
    /// above it. An exchange moves rows between threads, so thread t above it is not
    /// doing thread t's work below it; every consumer thread waits on all the producers
    /// and none finishes before the slowest does. This is the per-thread form of the
    /// serial path's max child (#680).
    ///
    /// CPU keeps the same-thread pairing. A consumer thread never burns a producer
    /// thread's CPU, so taking the slowest producer's CPU off every consumer thread would
    /// subtract CPU the thread never held. The pairing can only run low, never high, so
    /// it cannot crown the wrong operator the way the elapsed pairing did.
    /// </summary>
    private sealed record ThreadMetric(
        Func<PerThreadRuntimeInfo, long> Pick,
        Func<PlanNode, long> NodeTotal,
        bool SlowestProducerOffEveryThread)
    {
        public static readonly ThreadMetric Elapsed =
            new(t => t.ActualElapsedMs, n => n.ActualElapsedMs, SlowestProducerOffEveryThread: true);

        public static readonly ThreadMetric Cpu =
            new(t => t.ActualCPUMs, n => n.ActualCPUMs, SlowestProducerOffEveryThread: false);
    }

    /// <summary>
    /// Per-thread self-time calculation for parallel row mode operators.
    /// For each worker thread: self = parent[t] - sum(effective children[t]), less the
    /// slowest producer thread below any exchange among the children, which comes off
    /// every thread alike. Returns max across worker threads.
    /// </summary>
    private static long GetPerThreadOwnElapsed(PlanNode node) =>
        GetPerThreadOwn(node, ThreadMetric.Elapsed);

    private static long GetPerThreadOwn(PlanNode node, ThreadMetric metric) =>
        GetPerThreadOwnByThread(node, metric).Values.DefaultIfEmpty(0).Max();

    /// <summary>
    /// Self time on each worker thread: the thread's time minus the same thread's time
    /// in the children, minus the time that comes off every thread alike (the slowest
    /// producer below an exchange). Elapsed takes the max of these; total CPU takes the sum.
    /// </summary>
    private static Dictionary<int, long> GetPerThreadOwnByThread(PlanNode node, ThreadMetric metric)
    {
        var childSumByThread = new Dictionary<int, long>();
        var everyThreadMs = 0L;
        foreach (var child in node.Children)
        {
            // An exchange's own children run on the other side of it, on other threads.
            if (node.PhysicalOp == "Parallelism" && metric.SlowestProducerOffEveryThread)
                everyThreadMs += SlowestThread(child, metric);
            else
                AddEffectiveChildByThread(child, metric, childSumByThread, ref everyThreadMs);
        }

        var selfByThread = new Dictionary<int, long>();
        foreach (var ts in WorkThreads(node))
        {
            childSumByThread.TryGetValue(ts.ThreadId, out var childMs);
            selfByThread[ts.ThreadId] = Math.Max(0, metric.Pick(ts) - childMs - everyThreadMs);
        }

        return selfByThread;
    }

    /// <summary>
    /// The longest any one worker thread spent in a subtree that runs on the producer
    /// side of an exchange, by the same look-through rules as
    /// <see cref="AddEffectiveChildByThread"/>.
    /// </summary>
    private static long SlowestThread(PlanNode node, ThreadMetric metric)
    {
        var byThread = new Dictionary<int, long>();
        var everyThreadMs = 0L;
        AddEffectiveChildByThread(node, metric, byThread, ref everyThreadMs);
        return (byThread.Count > 0 ? byThread.Values.Max() : 0) + everyThreadMs;
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
    ///
    /// An exchange child whose metric takes the slowest producer off every thread adds
    /// to <paramref name="everyThreadMs"/> instead of <paramref name="acc"/>: what is
    /// below it ran on other threads (#680).
    /// </summary>
    private static void AddEffectiveChildByThread(
        PlanNode child,
        ThreadMetric metric,
        Dictionary<int, long> acc,
        ref long everyThreadMs)
    {
        if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
        {
            // The exchange's own time is not used. It soaks up time a spilling parent
            // spends (sql.kiwi/2021/03), and subtracting it would hand that time to the
            // exchange instead of the parent. Elapsed takes the slowest producer thread
            // below it off every thread above; CPU pairs the dominant producer by thread.
            if (metric.SlowestProducerOffEveryThread)
            {
                everyThreadMs += child.Children.Max(c => SlowestThread(c, metric));
            }
            else
            {
                var dominant = child.Children.OrderByDescending(metric.NodeTotal).First();
                AddEffectiveChildByThread(dominant, metric, acc, ref everyThreadMs);
            }
            return;
        }

        var mode = child.ActualExecutionMode ?? child.ExecutionMode;
        if (mode == "Batch" && child.HasActualStats)
        {
            AddBatchSubtreeByThread(child, metric, acc, ref everyThreadMs);
            return;
        }

        if (child.HasActualStats && metric.NodeTotal(child) > 0)
        {
            foreach (var ts in WorkThreads(child))
            {
                acc.TryGetValue(ts.ThreadId, out var existing);
                acc[ts.ThreadId] = existing + metric.Pick(ts);
            }
            return;
        }

        // No runtime stats: look through to the descendants that have them.
        foreach (var grandchild in child.Children)
            AddEffectiveChildByThread(grandchild, metric, acc, ref everyThreadMs);
    }

    /// <summary>
    /// Per-thread sum across a contiguous batch-mode zone, stopping at exchanges.
    /// Batch operators pipeline, so their times add rather than nest.
    /// </summary>
    private static void AddBatchSubtreeByThread(
        PlanNode node,
        ThreadMetric metric,
        Dictionary<int, long> acc,
        ref long everyThreadMs)
    {
        foreach (var ts in WorkThreads(node))
        {
            acc.TryGetValue(ts.ThreadId, out var existing);
            acc[ts.ThreadId] = existing + metric.Pick(ts);
        }

        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism") continue; // zone boundary

            var childMode = child.ActualExecutionMode ?? child.ExecutionMode;
            if (childMode == "Batch" && child.HasActualStats)
                AddBatchSubtreeByThread(child, metric, acc, ref everyThreadMs);
            else
                AddEffectiveChildByThread(child, metric, acc, ref everyThreadMs);
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

        // Batch mode reports each thread's own CPU directly. Subtracting the
        // children would take their standalone CPU off a number that never held it.
        // A node with no per-thread rows (built in code, not parsed) keeps its total.
        if ((node.ActualExecutionMode ?? node.ExecutionMode) == "Batch")
            return WorkThreads(node).Select(t => t.ActualCPUMs).DefaultIfEmpty(node.ActualCPUMs).Max();

        if (node.PerThreadStats.Count > 1)
            return GetPerThreadOwn(node, ThreadMetric.Cpu);

        // Serial: operator_cpu - Σ effective child cpu
        var totalChildCpu = 0L;
        foreach (var child in node.Children)
            totalChildCpu += GetEffectiveChildCpuMs(child);
        return Math.Max(0, node.ActualCPUMs - totalChildCpu);
    }

    /// <summary>
    /// The operator's own CPU, children subtracted, summed across threads. CPU sums
    /// because every thread burned its own, so this is the figure to set beside
    /// QueryTimeStats CpuTime, and self CPU above self elapsed means parallelism.
    /// Serial and batch operators get the same value as
    /// <see cref="GetOperatorMaxThreadOwnCpuMs"/>; a parallel row-mode operator gets
    /// all of its threads instead of the busiest one.
    /// </summary>
    internal static long GetOperatorOwnCpuMs(PlanNode node)
    {
        if (!node.HasActualStats || node.ActualCPUMs <= 0) return 0;

        if ((node.ActualExecutionMode ?? node.ExecutionMode) == "Batch")
            return node.ActualCPUMs;

        if (node.PerThreadStats.Count > 1)
            return GetPerThreadOwnByThread(node, ThreadMetric.Cpu).Values.Sum();

        var totalChildCpu = 0L;
        foreach (var child in node.Children)
            totalChildCpu += GetEffectiveChildCpuMs(child);
        return Math.Max(0, node.ActualCPUMs - totalChildCpu);
    }

    private static long GetEffectiveChildCpuMs(PlanNode child)
    {
        if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            return child.Children.Max(GetEffectiveChildCpuMs);

        // Batch CPU is standalone, exactly like batch elapsed, so subtract the whole
        // zone. Taking only the child's own value left the rest of the zone in a
        // row-mode parent: a serial Stream Aggregate over a batch Sort reported
        // 14,771 ms of self CPU next to 3,059 ms of self elapsed. The zone gives 3,112.
        var mode = child.ActualExecutionMode ?? child.ExecutionMode;
        if (mode == "Batch" && child.HasActualStats)
            return SumBatchSubtreeCpuMs(child);

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
    /// The CPU twin of <see cref="SumBatchSubtreeElapsedMs"/>: batch operators report
    /// standalone CPU, so a zone's CPU adds up. Stops at exchange zone boundaries.
    /// </summary>
    private static long SumBatchSubtreeCpuMs(PlanNode node)
    {
        long sum = node.ActualCPUMs;
        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism") continue;

            var childMode = child.ActualExecutionMode ?? child.ExecutionMode;
            if (childMode == "Batch" && child.HasActualStats)
                sum += SumBatchSubtreeCpuMs(child);
            else
                sum += GetEffectiveChildCpuMs(child);
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
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{discarded:N0} of {inputRows:N0} rows discarded ({pct:N0}%)"));
        }

        // Logical reads across the entire child subtree
        long totalReads = 0;
        foreach (var child in filterNode.Children)
            totalReads += SumSubtreeReads(child);
        if (totalReads > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{totalReads:N0} {(totalReads == 1 ? "logical read" : "logical reads")} below"));

        // Elapsed time: use the direct child's time (cumulative in row mode, includes its children)
        var childElapsed = filterNode.Children.Max(c => c.ActualElapsedMs);
        if (childElapsed > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{childElapsed:N0}ms elapsed below"));

        if (parts.Count == 0)
            return "";

        return string.Join("\n", parts.Select(p => "• " + p));
    }

    // Rule 33 only looks at tables with at least this many rows, and DetectCeGuess relies on that:
    // the equality guess is a power of the row count, so on a small table it lands on the fixed
    // guesses below, and from this size up it stays clear of them (0.3% and 5.6% at this size,
    // falling as the table grows).
    private const double CeGuessMinTableRows = 100_000;

    /* Rules 11 and 34 are about reads a scan does that an index could spare. Under this many rows a
       table is a few hundred pages, its scan is a buffer pool read of a millisecond or two, and the
       index either rule proposes costs more on every write than it saves on the read. */
    private const long ScanRuleMinRows = 10_000;

    /* An index that removed the scan outright saves at most the scan's own share of the statement's
       elapsed time. Under a tenth, the saving sits inside a statement's run-to-run variance and the
       finding points at nothing a person could measure. Rule 35 needs twice this share before it
       calls an operator expensive with nothing else to say about it. */
    private const double ScanRuleMinOwnTimeSharePct = 10;

    /* Rule 11 reports rows a scan read and then discarded. When at least half of the rows read were
       returned, a seek on the predicate columns would touch most of the same pages, so the predicate
       is not the cost the rule describes. */
    private const double ScanPredicateMaxSelectivity = 0.5;

    private static bool HasStatementElapsed(PlanStatement stmt) =>
        stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs > 0;

    /// <summary>
    /// The operator's own elapsed time as a share of the statement's, or null when the plan carries
    /// no statement time and there is no share to measure.
    /// </summary>
    private static double? OwnTimeSharePct(PlanNode node, PlanStatement stmt) =>
        HasStatementElapsed(stmt)
            ? (double)GetOperatorOwnElapsedMs(node) / stmt.QueryTimeStats!.ElapsedTimeMs * 100
            : null;

    /// <summary>
    /// How many times the scan read its table, for the TableCardinality stand-in. The parser sums
    /// ActualExecutions across threads, and a plain parallel scan reports one execution on each worker
    /// thread for one pass over the table, so a node with per-thread entries takes the largest
    /// per-thread count. That is exact for a parallel scan and undercounts a parallel Nested Loops inner
    /// side by at most the DOP, in the direction the floor already errs.
    /// </summary>
    private static long ScanExecutions(PlanNode node) =>
        node.PerThreadStats.Count > 1
            ? node.PerThreadStats.Max(t => t.ActualExecutions)
            : node.ActualExecutions;

    /// <summary>
    /// Rule 11's gate. An estimated plan is always reported. An actual plan must have read at least
    /// ScanRuleMinRows rows, returned under ScanPredicateMaxSelectivity of what it read when the plan
    /// says how many it read, and spent at least ScanRuleMinOwnTimeSharePct of the statement's time in
    /// the scan when the plan carries statement time. The rows floor reads ActualRowsRead when the plan
    /// carries it, the table's cardinality times the scan's executions when it carries that instead
    /// (ActualRowsRead is a total across executions, and TableCardinality is one pass over the table),
    /// and nothing when it carries neither: the rows returned are what the predicate kept, not what the
    /// scan read. The impact details come out either way, so the caller does not build them twice.
    /// </summary>
    private static bool ScanPredicateIsWorthReporting(PlanNode node, PlanStatement stmt, out ScanImpact details)
    {
        details = BuildScanImpactDetails(node, stmt);
        if (!node.HasActualStats)
            return true;

        if (node.HasActualRowsRead)
        {
            if (Math.Max(node.ActualRowsRead, node.ActualRows) < ScanRuleMinRows)
                return false;
        }
        else if (node.HasTableCardinality && node.TableCardinality * Math.Max(1, ScanExecutions(node)) < ScanRuleMinRows)
        {
            return false;
        }

        if (node.ActualRowsRead > 0 && (double)node.ActualRows / node.ActualRowsRead >= ScanPredicateMaxSelectivity)
            return false;

        var ownShare = OwnTimeSharePct(node, stmt);
        return ownShare is null || ownShare.Value >= ScanRuleMinOwnTimeSharePct;
    }

    /// <summary>
    /// Detects well-known CE default selectivity guesses by comparing EstimateRows to TableCardinality.
    /// Returns a description of the guess pattern, or null if no known pattern matches.
    ///
    /// Where each guess comes from was measured on SQL Server 2025: a 100,000-row heap with no
    /// statistics, estimated plans through SET SHOWPLAN_XML, CE 70 through
    /// FORCE_LEGACY_CARDINALITY_ESTIMATION and CE 120 to 170 through the compatibility level. The
    /// versions from 120 to 170 agree except where a row says otherwise.
    ///   equality, a = 5 or a IS NULL       CE 120+: rows^0.5        CE 70: rows^0.75
    ///   inequality, a &gt; 5                 30%, every CE
    ///   BETWEEN or a two-sided range       CE 70: 9%               CE 120+: 9% on a column with known values
    ///   LIKE                               CE 120+: 9%              CE 70: not a fixed guess
    ///   two inequalities, two columns      CE 120+: 16.43%          CE 70: 9%
    ///   range on variables, or on an expression, ABS(a) BETWEEN 5 AND 10   CE 120+: 16.43%   CE 70: 9%
    ///   one column compared with another   10%, every CE
    ///   equality on an expression, ABS(a) = 5   CE 130+: 10%        CE 120: rows^0.5    CE 70: rows^0.75
    ///   two 10% guesses, a = b AND c = d   CE 120+: 3.16%           CE 70: 1%
    /// The 16.43% is 30% times the square root of 30%, how CE 120+ combines two 30% guesses.
    /// </summary>
    /// <param name="ceModelVersion">
    /// The statement's CardinalityEstimationModelVersion: 70 is the legacy estimator, 120 and later
    /// the current one, and 0 means the plan did not say, so both stay possible.
    /// </param>
    private static string? DetectCeGuess(double estimateRows, double tableCardinality, int ceModelVersion = 0)
    {
        if (tableCardinality <= 0) return null;
        var selectivity = estimateRows / tableCardinality;
        var pct = string.Create(CultureInfo.InvariantCulture, $"{selectivity * 100:N1}%");
        var legacy = ceModelVersion == 70;
        var current = ceModelVersion >= 120;

        // Equality is not a fixed share of the table, so it is checked on its own, to 1%. The two
        // estimators use different powers of the row count, so a plan that names its estimator only
        // gets the one that estimator uses.
        static bool Near(double rows, double guess) => Math.Abs(rows - guess) <= guess * 0.01;

        if (!legacy && Near(estimateRows, Math.Sqrt(tableCardinality)))
            return $"matches the equality guess (an equality or IS NULL with no statistics to use), the square root of the row count ({pct})";
        if (!current && Near(estimateRows, Math.Pow(tableCardinality, 0.75)))
            return $"matches the equality guess (an equality or IS NULL with no statistics to use), the row count to the power 0.75 ({pct})";

        // The fixed guesses, with a 2% tolerance band
        return selectivity switch
        {
            >= 0.29 and <= 0.31 =>
                $"matches the 30% guess for an inequality such as > or < ({pct})",
            >= 0.088 and <= 0.092 =>
                current ? $"matches the 9% guess for BETWEEN or a two-sided range on a column with known values, or for LIKE ({pct})"
                : legacy ? $"matches the 9% guess for BETWEEN, a two-sided range, or two inequalities on different columns ({pct})"
                : $"matches the 9% guess for BETWEEN or a two-sided range ({pct})",
            >= 0.098 and <= 0.102 =>
                ceModelVersion is 0 or >= 130
                    ? $"matches the 10% guess for comparing one column with another, or for an equality on an expression such as a function of a column ({pct})"
                    : $"matches the 10% guess for comparing one column with another ({pct})",
            >= 0.155 and <= 0.175 when !legacy =>
                $"matches the 16.4% guess for two inequalities on different columns, or for a BETWEEN or range on variables or on an expression ({pct})",
            >= 0.009 and <= 0.011 when !current =>
                $"matches the 1% guess that CE 70 gets from multiplying two 10% guesses, as when two predicates each compare one column with another ({pct})",
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
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"This scan is {costPct:N0}% of the plan cost."));
        }

        // % of elapsed time (actual plans)
        double elapsedPct = 0;
        if (node.HasActualStats && node.ActualElapsedMs > 0 &&
            stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs > 0)
        {
            elapsedPct = (double)node.ActualElapsedMs / stmt.QueryTimeStats.ElapsedTimeMs * 100;
            if (elapsedPct >= 50)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"This scan took {elapsedPct:N0}% of elapsed time."));
        }

        // Row selectivity: rows returned vs rows read (actual) or vs table cardinality (estimated)
        if (node.HasActualStats && node.ActualRowsRead > 0 && node.ActualRows < node.ActualRowsRead)
        {
            var selectivity = (double)node.ActualRows / node.ActualRowsRead * 100;
            if (selectivity < 10)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"Only {selectivity:N3}% of rows survived filtering ({node.ActualRows:N0} of {node.ActualRowsRead:N0})."));
        }
        else if (!node.HasActualStats && node.TableCardinality > 0 && node.EstimateRows < node.TableCardinality)
        {
            var selectivity = node.EstimateRows / node.TableCardinality * 100;
            if (selectivity < 10)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"Only {selectivity:N1}% of rows estimated to survive filtering."));
        }

        return new ScanImpact(costPct, elapsedPct, parts.Count > 0 ? string.Join(" ", parts) : null);
    }
}
