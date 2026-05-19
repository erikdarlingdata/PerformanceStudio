using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static void MarkLegacyWarnings(PlanStatement stmt)
    {
        foreach (var w in stmt.PlanWarnings)
        {
            if (LegacyWarningTypes.Contains(w.WarningType))
                w.IsLegacy = true;
        }
        if (stmt.RootNode != null)
            MarkLegacyWarningsOnTree(stmt.RootNode);
    }

    private static void MarkLegacyWarningsOnTree(PlanNode node)
    {
        foreach (var w in node.Warnings)
        {
            if (LegacyWarningTypes.Contains(w.WarningType))
                w.IsLegacy = true;
        }
        foreach (var child in node.Children)
            MarkLegacyWarningsOnTree(child);
    }

    private static void ApplySeverityOverrides(ParsedPlan plan, AnalyzerConfig cfg)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                foreach (var w in stmt.PlanWarnings)
                    TryOverrideSeverity(w, cfg);

                if (stmt.RootNode != null)
                    ApplyOverridesToTree(stmt.RootNode, cfg);
            }
        }
    }

    private static void ApplyOverridesToTree(PlanNode node, AnalyzerConfig cfg)
    {
        foreach (var w in node.Warnings)
            TryOverrideSeverity(w, cfg);
        foreach (var child in node.Children)
            ApplyOverridesToTree(child, cfg);
    }

    private static void TryOverrideSeverity(PlanWarning warning, AnalyzerConfig cfg)
    {
        // Find the rule number for this warning type (partial match for flexibility)
        int? ruleNumber = null;
        foreach (var (rule, type) in RuleWarningTypes)
        {
            if (warning.WarningType.Contains(type, StringComparison.OrdinalIgnoreCase) ||
                type.Contains(warning.WarningType, StringComparison.OrdinalIgnoreCase))
            {
                ruleNumber = rule;
                break;
            }
        }

        if (ruleNumber == null) return;

        var overrideSeverity = cfg.GetSeverityOverride(ruleNumber.Value);
        if (overrideSeverity == null) return;

        if (Enum.TryParse<PlanWarningSeverity>(overrideSeverity, ignoreCase: true, out var severity))
            warning.Severity = severity;
    }

    /// Determines whether a row estimate mismatch actually caused observable harm.
    /// Returns a description of the harm, or null if the bad estimate is benign.
    ///
    /// False-positive suppression (from reviewer feedback):
    /// - Root node (no parent) — nothing above to be harmed by the bad estimate
    /// - Sort that didn't spill — the estimate was wrong but no harm done
    ///
    /// Real harm:
    /// - The node itself has a spill warning (bad estimate → bad memory grant)
    /// - The node is a join (wrong join type or excessive inner side work)
    /// - A parent join may have chosen the wrong strategy based on bad row count
    /// - A parent Sort/Hash spilled (downstream estimate caused bad grant)
    /// </summary>
    /// <summary>
    /// Returns a short label describing what a wait type means (e.g., "I/O — reading from disk").
    /// Public for use by UI components that annotate wait stats inline.
    /// </summary>
    public static string GetWaitLabel(string waitType)
    {
        var wt = waitType.ToUpperInvariant();
        return wt switch
        {
            _ when wt.StartsWith("PAGEIOLATCH") => "I/O — reading data from disk",
            _ when wt.Contains("IO_COMPLETION") => "I/O — spills to TempDB or eager writes",
            _ when wt == "SOS_SCHEDULER_YIELD" => "CPU — scheduler yielding",
            _ when wt.StartsWith("CXPACKET") || wt.StartsWith("CXCONSUMER") => "parallelism — thread skew",
            _ when wt.StartsWith("CXSYNC") => "parallelism — exchange synchronization",
            _ when wt == "HTBUILD" => "hash — building hash table",
            _ when wt == "HTDELETE" => "hash — cleaning up hash table",
            _ when wt == "HTREPARTITION" => "hash — repartitioning",
            _ when wt.StartsWith("HT") => "hash operation",
            _ when wt == "BPSORT" => "batch sort",
            _ when wt == "BMPBUILD" => "bitmap filter build",
            _ when wt.Contains("MEMORY_ALLOCATION_EXT") => "memory allocation",
            _ when wt.StartsWith("PAGELATCH") => "page latch — in-memory contention",
            _ when wt.StartsWith("LATCH_") => "latch contention",
            _ when wt.StartsWith("LCK_") => "lock contention",
            _ when wt == "LOGBUFFER" => "transaction log writes",
            _ when wt == "ASYNC_NETWORK_IO" => "network — client not consuming results",
            _ when wt == "SOS_PHYS_PAGE_CACHE" => "physical page cache contention",
            _ => ""
        };
    }

    /// <summary>
    /// Returns true if the statement has significant I/O waits (PAGEIOLATCH_*, IO_COMPLETION).
    /// Used for severity elevation decisions where I/O specifically indicates disk access.
    /// Thresholds: I/O waits >= 20% of total wait time AND >= 100ms absolute.
    /// </summary>
    private static bool HasSignificantIoWaits(List<WaitStatInfo> waits)
    {
        if (waits.Count == 0)
            return false;

        var totalMs = waits.Sum(w => w.WaitTimeMs);
        if (totalMs == 0)
            return false;

        long ioMs = 0;
        foreach (var w in waits)
        {
            var wt = w.WaitType.ToUpperInvariant();
            if (wt.StartsWith("PAGEIOLATCH") || wt.Contains("IO_COMPLETION"))
                ioMs += w.WaitTimeMs;
        }

        var pct = (double)ioMs / totalMs * 100;
        return ioMs >= 100 && pct >= 20;
    }

    private static bool AllocatesResources(PlanNode node)
    {
        // Operators that get memory grants or allocate structures based on row estimates.
        // Hash Match (hash table), Sort (sort buffer), Spool (worktable).
        var op = node.PhysicalOp;
        return op.StartsWith("Hash", StringComparison.OrdinalIgnoreCase)
            || op.StartsWith("Sort", StringComparison.OrdinalIgnoreCase)
            || op.EndsWith("Spool", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AssessEstimateHarm(PlanNode node, double ratio)
    {
        // Root node: no parent to harm.
        // The synthetic statement root (SELECT/INSERT/etc.) has NodeId == -1.
        if (node.Parent == null || node.Parent.NodeId == -1)
            return null;

        // The node itself has a spill — bad estimate caused bad memory grant
        if (HasSpillWarning(node))
        {
            return ratio >= 10.0
                ? "The underestimate likely caused an insufficient memory grant, leading to a spill to TempDB."
                : "The overestimate may have caused an excessive memory grant, wasting workspace memory.";
        }

        // Sort/Hash that did NOT spill — estimate was wrong but no observable harm
        if ((node.PhysicalOp.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
             node.PhysicalOp.Contains("Hash", StringComparison.OrdinalIgnoreCase)) &&
            !HasSpillWarning(node))
        {
            return null;
        }

        // The node is a join — bad estimate means wrong join type or excessive work
        // Adaptive joins (2017+) switch strategy at runtime, so the estimate didn't lock in a bad choice.
        if (node.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase) && !node.IsAdaptive)
        {
            return ratio >= 10.0
                ? "The underestimate may have caused the optimizer to make poor choices."
                : "The overestimate may have caused the optimizer to make poor choices.";
        }

        // Walk up to check if a parent was harmed by this bad estimate
        var ancestor = node.Parent;
        while (ancestor != null)
        {
            // Transparent operators — skip through
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
            // Adaptive joins handle this at runtime, so skip them.
            if (ancestor.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase))
            {
                if (ancestor.IsAdaptive)
                    return null; // Adaptive join self-corrects — no harm

                return ratio >= 10.0
                    ? $"The underestimate may have caused the optimizer to make poor choices."
                    : $"The overestimate may have caused the optimizer to make poor choices.";
            }

            // Parent Sort/Hash that spilled — downstream bad estimate caused the spill
            if (HasSpillWarning(ancestor))
            {
                return ratio >= 10.0
                    ? $"The underestimate contributed to {ancestor.PhysicalOp} (Node {ancestor.NodeId}) spilling to TempDB."
                    : $"The overestimate contributed to {ancestor.PhysicalOp} (Node {ancestor.NodeId}) receiving an excessive memory grant.";
            }

            // Parent Sort/Hash with no spill — benign
            if (ancestor.PhysicalOp.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
                ancestor.PhysicalOp.Contains("Hash", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Any other operator — stop walking
            break;
        }

        // Default: the estimate is off but we can't identify specific harm
        return null;
    }

    /// <summary>
    /// Checks if a node has any spill-related warnings (Sort/Hash/Exchange spills).
    /// </summary>
    private static bool HasSpillWarning(PlanNode node)
    {
        return node.Warnings.Any(w => w.SpillDetails != null);
    }

    /// <summary>
    /// Formats a node reference for use in warning messages. Includes object name
    /// for data access operators where it helps identify which table is involved.
    /// </summary>
    private static string FormatNodeRef(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            var objRef = !string.IsNullOrEmpty(node.DatabaseName)
                ? $"{node.DatabaseName}.{node.ObjectName}"
                : node.ObjectName;
            return $"{node.PhysicalOp} on {objRef} (Node {node.NodeId})";
        }

        return $"{node.PhysicalOp} (Node {node.NodeId})";
    }

    /// <summary>
    /// Identifies the specific cause of a row goal from the statement text.
    /// Returns a specific cause when detectable, or a generic list as fallback.
    /// </summary>
    private static string IdentifyRowGoalCause(string stmtText)
    {
        if (string.IsNullOrEmpty(stmtText))
            return "TOP, EXISTS, IN, or FAST hint";

        var text = stmtText.ToUpperInvariant();
        var causes = new List<string>(4);

        if (Regex.IsMatch(text, @"\bTOP\b"))
            causes.Add("TOP");
        if (Regex.IsMatch(text, @"\bEXISTS\b"))
            causes.Add("EXISTS");
        // IN with subquery — bare "IN (" followed by SELECT, not just "IN (1,2,3)"
        if (Regex.IsMatch(text, @"\bIN\s*\(\s*SELECT\b"))
            causes.Add("IN (subquery)");
        if (Regex.IsMatch(text, @"\bFAST\b"))
            causes.Add("FAST hint");

        return causes.Count > 0
            ? string.Join(", ", causes)
            : "TOP, EXISTS, IN, or FAST hint";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
