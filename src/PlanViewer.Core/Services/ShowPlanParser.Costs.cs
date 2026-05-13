using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class ShowPlanParser
{
    private static void ComputeOperatorCosts(ParsedPlan plan)
    {
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                if (stmt.RootNode == null) continue;
                var totalCost = stmt.StatementSubTreeCost > 0
                    ? stmt.StatementSubTreeCost
                    : stmt.RootNode.EstimatedTotalSubtreeCost;
                if (totalCost <= 0) totalCost = 1;
                ComputeNodeCosts(stmt.RootNode, totalCost);
            }
        }
    }

    private static void ComputeNodeCosts(PlanNode node, double totalStatementCost)
    {
        var childrenSubtreeCost = node.Children.Sum(c => c.EstimatedTotalSubtreeCost);
        node.EstimatedOperatorCost = Math.Max(0, node.EstimatedTotalSubtreeCost - childrenSubtreeCost);
        node.CostPercent = (int)Math.Round((node.EstimatedOperatorCost / totalStatementCost) * 100);
        node.CostPercent = Math.Min(100, Math.Max(0, node.CostPercent));

        foreach (var child in node.Children)
            ComputeNodeCosts(child, totalStatementCost);
    }
}
