using System;
using System.Collections.Generic;
using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;

namespace PlanViewer.Web;

/// <summary>
/// Stateless formatting and predicate helpers shared across the web plan-viewer
/// page and its child components (operator properties panel, insights, warnings).
/// Pulled out of Index.razor so the same helper isn't duplicated per component;
/// imported via <c>@using static PlanViewer.Web.PlanViewFormat</c> in _Imports.razor
/// so call sites read identically to when these were local methods.
/// </summary>
public static class PlanViewFormat
{
    public static string FormatKB(long kb)
    {
        if (kb < 1024) return $"{kb:N0} KB";
        if (kb < 1024 * 1024) return $"{kb / 1024.0:N1} MB";
        return $"{kb / (1024.0 * 1024.0):N2} GB";
    }

    public static string FormatMs(long ms)
    {
        if (ms < 1000) return $"{ms:N0} ms";
        return $"{ms / 1000.0:F3} s";
    }

    public static string FormatTtl(int days) => days switch
    {
        1 => "1 day",
        < 30 => $"{days} days",
        30 => "1 month",
        90 => "3 months",
        180 => "6 months",
        365 => "1 year",
        _ => $"{days} days"
    };

    // Memory grant color tiers (#215 C1 + E8 + E9):
    // > 100%: over-used grant (red). Spill in plan: orange. Otherwise: tier by utilization.
    public static string GetMemoryGrantColorClass(double pctUsed, bool hasSpill)
    {
        if (pctUsed > 100) return "eff-bad";
        if (hasSpill) return "eff-warn";
        if (pctUsed >= 40) return "eff-good";
        if (pctUsed >= 20) return "eff-warn";
        return "eff-bad";
    }

    public static bool HasSpillInTree(OperatorResult? node)
    {
        if (node == null) return false;
        foreach (var w in node.Warnings)
            if (w.Type.EndsWith(" Spill", StringComparison.Ordinal)) return true;
        foreach (var child in node.Children)
            if (HasSpillInTree(child)) return true;
        return false;
    }

    public static List<WarningResult> GetAllWarnings(StatementResult stmt)
    {
        var warnings = new List<WarningResult>(stmt.Warnings);
        if (stmt.OperatorTree != null)
            CollectNodeWarningsRecursive(stmt.OperatorTree, warnings);
        return warnings;
    }

    public static List<WarningResult> CollectNodeWarnings(OperatorResult node)
    {
        var warnings = new List<WarningResult>();
        CollectNodeWarningsRecursive(node, warnings);
        return warnings;
    }

    public static void CollectNodeWarningsRecursive(OperatorResult node, List<WarningResult> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarningsRecursive(child, warnings);
    }

    public static string BuildTooltip(PlanNode node)
    {
        var parts = new List<string>();
        parts.Add($"{node.PhysicalOp} (Node {node.NodeId})");

        if (!string.IsNullOrEmpty(node.LogicalOp) && node.LogicalOp != node.PhysicalOp)
            parts.Add($"Logical: {node.LogicalOp}");

        if (node.HasActualStats)
        {
            parts.Add($"Actual rows: {node.ActualRows:N0}");
            parts.Add($"Estimated rows: {node.EstimateRows:N0}");
            if (node.ActualLogicalReads > 0) parts.Add($"Logical reads: {node.ActualLogicalReads:N0}");
            if (node.ActualPhysicalReads > 0) parts.Add($"Physical reads: {node.ActualPhysicalReads:N0}");
        }
        else
        {
            parts.Add($"Estimated rows: {node.EstimateRows:N0}");
        }

        parts.Add($"Estimated cost: {node.EstimatedOperatorCost:N4}");
        parts.Add($"Subtree cost: {node.EstimatedTotalSubtreeCost:N4}");

        if (!string.IsNullOrEmpty(node.ObjectName)) parts.Add($"Object: {node.FullObjectName ?? node.ObjectName}");
        if (!string.IsNullOrEmpty(node.IndexName)) parts.Add($"Index: {node.IndexName}");
        if (!string.IsNullOrEmpty(node.SeekPredicates)) parts.Add($"Seek: {node.SeekPredicates}");
        if (!string.IsNullOrEmpty(node.Predicate)) parts.Add($"Predicate: {node.Predicate}");
        if (!string.IsNullOrEmpty(node.OrderBy)) parts.Add($"Order by: {node.OrderBy}");
        if (!string.IsNullOrEmpty(node.OutputColumns)) parts.Add($"Output: {node.OutputColumns}");

        foreach (var w in node.Warnings)
            parts.Add($"[{w.Severity}] {w.WarningType}: {w.Message}");

        return string.Join("\n", parts);
    }

    public static string GetOperatorLabel(PlanNode node)
    {
        if (node.PhysicalOp == "Parallelism" && !string.IsNullOrEmpty(node.LogicalOp) && node.LogicalOp != "Parallelism")
            return $"Parallelism ({node.LogicalOp})";
        return node.PhysicalOp;
    }

    public static bool HasPredicates(PlanNode node) =>
        !string.IsNullOrEmpty(node.SeekPredicates) ||
        !string.IsNullOrEmpty(node.Predicate) ||
        !string.IsNullOrEmpty(node.HashKeysProbe) ||
        !string.IsNullOrEmpty(node.HashKeysBuild) ||
        !string.IsNullOrEmpty(node.BuildResidual) ||
        !string.IsNullOrEmpty(node.ProbeResidual) ||
        !string.IsNullOrEmpty(node.MergeResidual) ||
        !string.IsNullOrEmpty(node.PassThru) ||
        !string.IsNullOrEmpty(node.SetPredicate);

    public static bool HasOperatorDetails(PlanNode node) =>
        !string.IsNullOrEmpty(node.OrderBy) ||
        !string.IsNullOrEmpty(node.GroupBy) ||
        !string.IsNullOrEmpty(node.TopExpression) ||
        !string.IsNullOrEmpty(node.InnerSideJoinColumns) ||
        !string.IsNullOrEmpty(node.OuterSideJoinColumns) ||
        !string.IsNullOrEmpty(node.OuterReferences) ||
        !string.IsNullOrEmpty(node.DefinedValues) ||
        !string.IsNullOrEmpty(node.HashKeys) ||
        !string.IsNullOrEmpty(node.PartitionColumns) ||
        !string.IsNullOrEmpty(node.SegmentColumn) ||
        !string.IsNullOrEmpty(node.ConstantScanValues) ||
        !string.IsNullOrEmpty(node.ActionColumn) ||
        !string.IsNullOrEmpty(node.OriginalActionColumn) ||
        !string.IsNullOrEmpty(node.OffsetExpression) ||
        !string.IsNullOrEmpty(node.TvfParameters) ||
        !string.IsNullOrEmpty(node.UdxName) ||
        !string.IsNullOrEmpty(node.UdxUsedColumns) ||
        !string.IsNullOrEmpty(node.TieColumns) ||
        !string.IsNullOrEmpty(node.PartitioningType) ||
        !string.IsNullOrEmpty(node.PartitionId) ||
        !string.IsNullOrEmpty(node.StarJoinOperationType) ||
        !string.IsNullOrEmpty(node.ProbeColumn) ||
        node.ManyToMany || node.SortDistinct || node.BitmapCreator ||
        node.NLOptimized || node.WithOrderedPrefetch || node.WithUnorderedPrefetch ||
        node.Remoting || node.LocalParallelism || node.StartupExpression ||
        node.DMLRequestSort || node.SpoolStack || node.WithTies ||
        node.IsStarJoin || node.InRow || node.ComputeSequence ||
        node.RowCount || node.GroupExecuted || node.RemoteDataAccess ||
        node.OptimizedHalloweenProtectionUsed ||
        node.NonClusteredIndexCount > 0 || node.TopRows > 0 ||
        node.RollupHighestLevel > 0 || node.ForceSeekColumnCount > 0 ||
        node.StatsCollectionId > 0;

    public static bool HasMemoryInfo(PlanNode node) =>
        (node.MemoryGrantKB.HasValue && node.MemoryGrantKB > 0) ||
        (node.DesiredMemoryKB.HasValue && node.DesiredMemoryKB > 0) ||
        (node.MaxUsedMemoryKB.HasValue && node.MaxUsedMemoryKB > 0) ||
        node.InputMemoryGrantKB > 0 ||
        node.OutputMemoryGrantKB > 0 ||
        node.UsedMemoryGrantKB > 0 ||
        node.MemoryFractionInput > 0 ||
        node.MemoryFractionOutput > 0;
}
