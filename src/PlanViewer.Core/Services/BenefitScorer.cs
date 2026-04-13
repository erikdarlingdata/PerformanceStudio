using System;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Second-pass analysis that calculates MaxBenefitPercent for each PlanWarning.
/// Runs after PlanAnalyzer.Analyze() — the analyzer creates findings, the scorer quantifies them.
/// Benefit = maximum % of elapsed time that could be saved by addressing the finding.
/// </summary>
public static class BenefitScorer
{
    // Warning types that map to specific scoring strategies
    private static readonly HashSet<string> OperatorTimeRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Filter Operator",      // Rule 1
        "Eager Index Spool",    // Rule 2
        "Spill",                // Rule 7
        "Key Lookup",           // Rule 10
        "RID Lookup",           // Rule 10 variant
        "Scan With Predicate",  // Rule 11
        "Non-SARGable Predicate", // Rule 12
        "Scan Cardinality Misestimate", // Rule 32
    };

    public static void Score(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                ScoreStatementWarnings(stmt);

                if (stmt.RootNode != null)
                    ScoreNodeTree(stmt.RootNode, stmt);
            }
        }
    }

    private static void ScoreStatementWarnings(PlanStatement stmt)
    {
        var elapsedMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

        foreach (var warning in stmt.PlanWarnings)
        {
            switch (warning.WarningType)
            {
                case "Ineffective Parallelism":   // Rule 25
                case "Parallel Wait Bottleneck":  // Rule 31
                    // These are meta-findings about parallelism efficiency.
                    // The benefit is the gap between actual and ideal elapsed time.
                    if (elapsedMs > 0 && stmt.QueryTimeStats != null)
                    {
                        var cpu = stmt.QueryTimeStats.CpuTimeMs;
                        var dop = stmt.DegreeOfParallelism;
                        if (dop > 1 && cpu > 0)
                        {
                            // Ideal elapsed = CPU / DOP. Benefit = (actual - ideal) / actual
                            var idealElapsed = (double)cpu / dop;
                            var benefit = Math.Max(0, (elapsedMs - idealElapsed) / elapsedMs * 100);
                            warning.MaxBenefitPercent = Math.Min(100, Math.Round(benefit, 1));
                        }
                    }
                    break;

                case "Serial Plan": // Rule 3
                    // Can't know how fast a parallel plan would be, but estimate:
                    // CPU-bound: benefit up to (1 - 1/maxDOP) * 100%
                    if (elapsedMs > 0 && stmt.QueryTimeStats != null)
                    {
                        var cpu = stmt.QueryTimeStats.CpuTimeMs;
                        // Assume server max DOP — use a conservative 4 if unknown
                        var potentialDop = 4;
                        if (cpu >= elapsedMs)
                        {
                            // CPU-bound: parallelism could help significantly
                            var benefit = (1.0 - 1.0 / potentialDop) * 100;
                            warning.MaxBenefitPercent = Math.Round(benefit, 1);
                        }
                        else
                        {
                            // Not CPU-bound: parallelism helps less
                            var cpuRatio = (double)cpu / elapsedMs;
                            var benefit = cpuRatio * (1.0 - 1.0 / potentialDop) * 100;
                            warning.MaxBenefitPercent = Math.Round(Math.Min(50, benefit), 1);
                        }
                    }
                    break;

                case "Memory Grant": // Rule 9
                    // Grant wait is the only part that affects this query's elapsed time
                    if (elapsedMs > 0 && stmt.MemoryGrant?.GrantWaitTimeMs > 0)
                    {
                        var benefit = (double)stmt.MemoryGrant.GrantWaitTimeMs / elapsedMs * 100;
                        warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
                    }
                    break;

                case "High Compile CPU": // Rule 19
                    if (elapsedMs > 0 && stmt.CompileCPUMs > 0)
                    {
                        var benefit = (double)stmt.CompileCPUMs / elapsedMs * 100;
                        warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
                    }
                    break;

                // Rules that cannot be quantified: leave MaxBenefitPercent as null
                // Rule 18 (Compile Memory Exceeded), Rule 20 (Local Variables),
                // Rule 27 (Optimize For Unknown)
            }
        }
    }

    private static void ScoreNodeTree(PlanNode node, PlanStatement stmt)
    {
        ScoreNodeWarnings(node, stmt);

        foreach (var child in node.Children)
            ScoreNodeTree(child, stmt);
    }

    private static void ScoreNodeWarnings(PlanNode node, PlanStatement stmt)
    {
        var elapsedMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

        foreach (var warning in node.Warnings)
        {
            // Already scored (e.g., by a different pass)
            if (warning.MaxBenefitPercent != null)
                continue;

            if (warning.WarningType == "UDF Execution") // Rule 4
            {
                ScoreUdfWarning(warning, node, elapsedMs);
            }
            else if (warning.WarningType == "Filter Operator") // Rule 1
            {
                ScoreFilterWarning(warning, node, stmt);
            }
            else if (warning.WarningType == "Nested Loops High Executions") // Rule 16
            {
                ScoreNestedLoopsWarning(warning, node, stmt);
            }
            else if (warning.SpillDetails != null) // Rule 7
            {
                ScoreSpillWarning(warning, node, stmt);
            }
            else if (OperatorTimeRules.Contains(warning.WarningType))
            {
                ScoreByOperatorTime(warning, node, stmt);
            }
            else if (warning.WarningType == "Row Estimate Mismatch") // Rule 5
            {
                ScoreEstimateMismatchWarning(warning, node, stmt);
            }
            // Rules that stay null: Scalar UDF (Rule 6, informational reference),
            // Parallel Skew (Rule 8), Data Type Mismatch (Rule 13),
            // Lazy Spool Ineffective (Rule 14), Join OR Clause (Rule 15),
            // Many-to-Many Merge Join (Rule 17), CTE Multiple References (Rule 21),
            // Table Variable (Rule 22), Table-Valued Function (Rule 23),
            // Top Above Scan (Rule 24), Row Goal (Rule 26),
            // NOT IN with Nullable Column (Rule 28), Implicit Conversion (Rule 29),
            // Wide Index Suggestion (Rule 30), Estimated Plan CE Guess (Rule 33)
        }
    }

    /// <summary>
    /// Rule 4: UDF Execution — benefit is UDF elapsed time / statement elapsed.
    /// </summary>
    private static void ScoreUdfWarning(PlanWarning warning, PlanNode node, long stmtElapsedMs)
    {
        if (stmtElapsedMs > 0 && node.UdfElapsedTimeMs > 0)
        {
            var benefit = (double)node.UdfElapsedTimeMs / stmtElapsedMs * 100;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
    }

    /// <summary>
    /// Rule 1: Filter Operator — benefit is child subtree elapsed / statement elapsed.
    /// The filter discards rows late; eliminating it means the child subtree work was unnecessary.
    /// </summary>
    private static void ScoreFilterWarning(PlanWarning warning, PlanNode node, PlanStatement stmt)
    {
        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

        if (node.HasActualStats && stmtMs > 0 && node.Children.Count > 0)
        {
            var childElapsed = node.Children.Max(c => c.ActualElapsedMs);
            var benefit = (double)childElapsed / stmtMs * 100;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
        else if (!node.HasActualStats && stmt.StatementSubTreeCost > 0 && node.Children.Count > 0)
        {
            // Estimated plan fallback: child subtree cost / statement cost
            var childCost = node.Children.Sum(c => c.EstimatedTotalSubtreeCost);
            var benefit = childCost / stmt.StatementSubTreeCost * 100;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
    }

    /// <summary>
    /// Rule 16: Nested Loops High Executions — benefit is inner-side elapsed / statement elapsed.
    /// </summary>
    private static void ScoreNestedLoopsWarning(PlanWarning warning, PlanNode node, PlanStatement stmt)
    {
        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

        if (node.Children.Count >= 2)
        {
            var innerChild = node.Children[1];

            if (innerChild.HasActualStats && stmtMs > 0 && innerChild.ActualElapsedMs > 0)
            {
                var benefit = (double)innerChild.ActualElapsedMs / stmtMs * 100;
                warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
            }
            else if (!innerChild.HasActualStats && stmt.StatementSubTreeCost > 0)
            {
                var benefit = innerChild.EstimatedTotalSubtreeCost / stmt.StatementSubTreeCost * 100;
                warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
            }
        }
    }

    /// <summary>
    /// Rule 7: Spill — benefit is the spilling operator's self-time / statement elapsed.
    /// Exchange spills use the parallelism operator time (unreliable but best we have).
    /// </summary>
    private static void ScoreSpillWarning(PlanWarning warning, PlanNode node, PlanStatement stmt)
    {
        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
        if (stmtMs <= 0) return;

        long operatorMs;
        if (warning.SpillDetails?.SpillType == "Exchange")
            operatorMs = GetParallelismOperatorElapsedMs(node);
        else
            operatorMs = PlanAnalyzer.GetOperatorOwnElapsedMs(node);

        if (operatorMs > 0)
        {
            var benefit = (double)operatorMs / stmtMs * 100;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
    }

    /// <summary>
    /// Generic operator-time scoring for rules where the fix would eliminate or
    /// drastically reduce the operator's work: Key Lookup, RID Lookup,
    /// Scan With Predicate, Non-SARGable Predicate, Eager Index Spool,
    /// Scan Cardinality Misestimate.
    /// </summary>
    private static void ScoreByOperatorTime(PlanWarning warning, PlanNode node, PlanStatement stmt)
    {
        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

        if (node.HasActualStats && stmtMs > 0)
        {
            var operatorMs = PlanAnalyzer.GetOperatorOwnElapsedMs(node);
            if (operatorMs > 0)
            {
                var benefit = (double)operatorMs / stmtMs * 100;
                warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
            }
            else
            {
                // Operator self-time is 0 — negligible benefit
                warning.MaxBenefitPercent = 0;
            }
        }
        else if (!node.HasActualStats && stmt.StatementSubTreeCost > 0)
        {
            // Estimated plan fallback: use operator cost percentage
            var benefit = (double)node.CostPercent;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
    }

    /// <summary>
    /// Rule 5: Row Estimate Mismatch — benefit is the harmed operator's time.
    /// If the mismatch caused a spill, benefit = spilling operator time.
    /// If it caused a bad join choice, benefit = join operator time.
    /// Otherwise, benefit is the misestimated operator's own time (conservative).
    /// </summary>
    private static void ScoreEstimateMismatchWarning(PlanWarning warning, PlanNode node, PlanStatement stmt)
    {
        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
        if (stmtMs <= 0 || !node.HasActualStats) return;

        // Walk up to find the harmed operator (same logic as AssessEstimateHarm)
        var harmedNode = FindHarmedOperator(node);
        if (harmedNode != null)
        {
            var operatorMs = PlanAnalyzer.GetOperatorOwnElapsedMs(harmedNode);
            if (operatorMs > 0)
            {
                var benefit = (double)operatorMs / stmtMs * 100;
                warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
                return;
            }
        }

        // Fallback: use the misestimated node's own time
        var ownMs = PlanAnalyzer.GetOperatorOwnElapsedMs(node);
        if (ownMs > 0)
        {
            var benefit = (double)ownMs / stmtMs * 100;
            warning.MaxBenefitPercent = Math.Round(Math.Min(100, benefit), 1);
        }
    }

    /// <summary>
    /// Walks up from a node with a bad row estimate to find the operator that was
    /// harmed by it (spilling sort/hash, or join that chose the wrong strategy).
    /// Returns null if no specific harm can be attributed.
    /// </summary>
    private static PlanNode? FindHarmedOperator(PlanNode node)
    {
        // The node itself has a spill — it harmed itself
        if (node.Warnings.Any(w => w.SpillDetails != null))
            return node;

        // Walk up through transparent operators
        var ancestor = node.Parent;
        while (ancestor != null)
        {
            if (ancestor.PhysicalOp == "Parallelism" ||
                ancestor.PhysicalOp == "Compute Scalar" ||
                ancestor.PhysicalOp == "Segment" ||
                ancestor.PhysicalOp == "Sequence Project" ||
                ancestor.PhysicalOp == "Top" ||
                ancestor.PhysicalOp == "Filter")
            {
                ancestor = ancestor.Parent;
                continue;
            }

            // Parent join — bad row count from below caused wrong join choice
            if (ancestor.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase))
            {
                if (ancestor.IsAdaptive)
                    return null; // Adaptive join self-corrects
                return ancestor;
            }

            // Parent Sort/Hash that spilled
            if (ancestor.Warnings.Any(w => w.SpillDetails != null))
                return ancestor;

            // Parent Sort/Hash with no spill — benign
            if (ancestor.PhysicalOp.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
                ancestor.PhysicalOp.Contains("Hash", StringComparison.OrdinalIgnoreCase))
                return null;

            break;
        }

        return null;
    }

    /// <summary>
    /// Calculates a Parallelism (exchange) operator's own elapsed time.
    /// Mirrors PlanAnalyzer.GetParallelismOperatorElapsedMs but accessible here.
    /// </summary>
    private static long GetParallelismOperatorElapsedMs(PlanNode node)
    {
        if (node.Children.Count == 0)
            return node.ActualElapsedMs;

        if (node.PerThreadStats.Count > 1)
            return PlanAnalyzer.GetOperatorOwnElapsedMs(node);

        var maxChildElapsed = node.Children.Max(c => c.ActualElapsedMs);
        return Math.Max(0, node.ActualElapsedMs - maxChildElapsed);
    }
}
