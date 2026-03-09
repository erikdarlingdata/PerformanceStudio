using System.IO;
using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Shared helper for loading and analyzing test plan files.
/// </summary>
public static class PlanTestHelper
{
    /// <summary>
    /// Loads a .sqlplan file from the Plans directory, parses it, and runs the analyzer.
    /// </summary>
    public static ParsedPlan LoadAndAnalyze(string planFileName)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        // SSMS saves plans as UTF-16 with encoding="utf-16" in the XML declaration.
        // File.ReadAllText auto-detects BOM, but XDocument.Parse chokes on the declaration.
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    /// <summary>
    /// Gets all warnings across all statements and all nodes in the plan.
    /// </summary>
    public static List<PlanWarning> AllWarnings(ParsedPlan plan)
    {
        var warnings = new List<PlanWarning>();

        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                warnings.AddRange(stmt.PlanWarnings);

                if (stmt.RootNode != null)
                    CollectNodeWarnings(stmt.RootNode, warnings);
            }
        }

        return warnings;
    }

    /// <summary>
    /// Gets all warnings of a specific type.
    /// </summary>
    public static List<PlanWarning> WarningsOfType(ParsedPlan plan, string warningType)
    {
        return AllWarnings(plan)
            .Where(w => w.WarningType == warningType)
            .ToList();
    }

    /// <summary>
    /// Gets the first statement in the plan.
    /// </summary>
    public static PlanStatement FirstStatement(ParsedPlan plan)
    {
        return plan.Batches.First().Statements.First();
    }

    /// <summary>
    /// Finds a node by NodeId in the plan tree.
    /// </summary>
    public static PlanNode? FindNode(PlanNode root, int nodeId)
    {
        if (root.NodeId == nodeId) return root;
        foreach (var child in root.Children)
        {
            var found = FindNode(child, nodeId);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectNodeWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }
}
