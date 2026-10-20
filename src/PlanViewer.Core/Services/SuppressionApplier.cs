using System;
using System.Collections.Generic;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Flags findings that a suppression in .planview.json matches (#651). It runs after the
/// scorer so the scorer's wait-stats findings can be suppressed too. It only sets
/// <see cref="PlanWarning.IsSuppressed"/> and <see cref="PlanWarning.SuppressionReason"/>:
/// no warning is removed and no severity changes.
/// </summary>
internal static class SuppressionApplier
{
    internal static void Apply(ParsedPlan plan, AnalyzerConfig config)
    {
        var entries = config.Suppressions;
        if (entries == null || entries.Count == 0)
            return;

        foreach (var stmt in PlanStatements.EnumerateAll(plan))
        {
            var hash = SuppressionEntry.NormalizeHash(stmt.QueryHash);
            if (hash.Length == 0)
                continue;

            var forStatement = new List<SuppressionEntry>();
            foreach (var entry in entries)
            {
                if (entry != null &&
                    string.Equals(SuppressionEntry.NormalizeHash(entry.QueryHash), hash, StringComparison.OrdinalIgnoreCase))
                    forStatement.Add(entry);
            }
            if (forStatement.Count == 0)
                continue;

            foreach (var w in stmt.PlanWarnings)
                TryApply(w, null, forStatement);

            if (stmt.RootNode != null)
                ApplyToTree(stmt.RootNode, forStatement);
        }
    }

    private static void ApplyToTree(PlanNode root, List<SuppressionEntry> entries)
    {
        var pending = new Stack<PlanNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.Warnings.Count > 0)
            {
                var label = OperatorLabel.Format(node);
                foreach (var w in node.Warnings)
                    TryApply(w, label, entries);
            }
            foreach (var child in node.Children)
                pending.Push(child);
        }
    }

    private static void TryApply(PlanWarning warning, string? operatorLabel, List<SuppressionEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!Matches(warning, operatorLabel, entry))
                continue;
            warning.IsSuppressed = true;
            warning.SuppressionReason = entry.Reason;
            return;
        }
    }

    private static bool Matches(PlanWarning warning, string? operatorLabel, SuppressionEntry entry)
    {
        if (entry.Rule is int rule && warning.RuleNumber != rule)
            return false;

        if (!string.IsNullOrWhiteSpace(entry.Type) &&
            !string.Equals(warning.WarningType, entry.Type.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(entry.Operator))
        {
            if (operatorLabel == null)
                return false;
            if (operatorLabel.IndexOf(entry.Operator.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }
}
