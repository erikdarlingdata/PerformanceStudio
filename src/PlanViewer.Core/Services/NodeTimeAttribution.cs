using System;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Computes a plan operator's "own" (self) CPU and elapsed time from its
/// cumulative actual stats by subtracting child contributions in row mode.
/// Batch mode reports self-time directly; row mode is cumulative from the leaves up.
/// Shared by the desktop and web plan viewers so both surfaces report identical
/// per-operator timings.
/// </summary>
/// <remarks>
/// This is the display-grade attribution used for node coloring and the
/// properties panel. The analysis pipeline uses the more elaborate per-thread
/// calculation in <see cref="PlanAnalyzer"/> (GetOperatorOwnElapsedMs /
/// GetOperatorMaxThreadOwnCpuMs); the two are intentionally separate.
/// </remarks>
public static class NodeTimeAttribution
{
    /// <summary>
    /// Computes own CPU time for a node by subtracting child times in row mode.
    /// Batch mode reports own time directly; row mode is cumulative from leaves up.
    /// </summary>
    public static long GetOwnCpuMs(PlanNode node)
    {
        if (node.ActualCPUMs <= 0) return 0;
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (mode == "Batch") return node.ActualCPUMs;
        var childSum = GetChildCpuMsSum(node);
        return Math.Max(0, node.ActualCPUMs - childSum);
    }

    /// <summary>
    /// Computes own elapsed time for a node by subtracting child times in row mode.
    /// </summary>
    public static long GetOwnElapsedMs(PlanNode node)
    {
        if (node.ActualElapsedMs <= 0) return 0;
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (mode == "Batch") return node.ActualElapsedMs;

        // Exchange operators: Thread 0 is the coordinator whose elapsed time is the
        // wall clock for the entire parallel branch — not the operator's own work.
        if (IsExchangeOperator(node))
        {
            // If we have worker thread data, use max of worker threads
            var workerMax = node.PerThreadStats
                .Where(t => t.ThreadId > 0)
                .Select(t => t.ActualElapsedMs)
                .DefaultIfEmpty(0)
                .Max();
            if (workerMax > 0)
            {
                var childSum = GetChildElapsedMsSum(node);
                return Math.Max(0, workerMax - childSum);
            }
            // Thread 0 only (coordinator) — exchange does negligible own work
            return 0;
        }

        var childElapsedSum = GetChildElapsedMsSum(node);
        return Math.Max(0, node.ActualElapsedMs - childElapsedSum);
    }

    /// <summary>
    /// True for parallelism exchange operators (Gather/Distribute/Repartition Streams),
    /// whose coordinator-thread timings don't reflect the operator's own work.
    /// </summary>
    public static bool IsExchangeOperator(PlanNode node) =>
        node.PhysicalOp == "Parallelism"
        || node.LogicalOp is "Gather Streams" or "Distribute Streams" or "Repartition Streams";

    private static long GetChildCpuMsSum(PlanNode node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.ActualCPUMs > 0)
                sum += child.ActualCPUMs;
            else
                sum += GetChildCpuMsSum(child); // skip through transparent operators
        }
        return sum;
    }

    private static long GetChildElapsedMsSum(PlanNode node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            {
                // Exchange: take max of children (parallel branches)
                sum += child.Children
                    .Where(c => c.ActualElapsedMs > 0)
                    .Select(c => c.ActualElapsedMs)
                    .DefaultIfEmpty(0)
                    .Max();
            }
            else if (child.ActualElapsedMs > 0)
            {
                sum += child.ActualElapsedMs;
            }
            else
            {
                sum += GetChildElapsedMsSum(child); // skip through transparent operators
            }
        }
        return sum;
    }
}
