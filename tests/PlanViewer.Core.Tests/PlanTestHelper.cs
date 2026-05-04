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
        return LoadAndAnalyze(planFileName, serverMetadata: null);
    }

    /// <summary>
    /// Loads a .sqlplan file from the Plans directory, parses it, and runs the analyzer
    /// with optional server metadata (for rules that depend on server context).
    /// </summary>
    public static ParsedPlan LoadAndAnalyze(string planFileName, ServerMetadata? serverMetadata)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        // SSMS saves plans as UTF-16 with encoding="utf-16" in the XML declaration.
        // File.ReadAllText auto-detects BOM, but XDocument.Parse chokes on the declaration.
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan, serverMetadata: serverMetadata);
        BenefitScorer.Score(plan);
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

    /// <summary>
    /// Loads a plan from .internal/examples (private plans not committed to git).
    /// Returns null if the file doesn't exist so tests can skip gracefully.
    /// </summary>
    public static ParsedPlan? LoadFromInternal(string planFileName)
    {
        // Walk up from bin/Debug/net8.0 to find the repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".internal")))
            dir = dir.Parent;
        if (dir == null) return null;

        var path = Path.Combine(dir.FullName, ".internal", "examples", planFileName);
        if (!File.Exists(path)) return null;

        var xml = File.ReadAllText(path);
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Gets all node-level warnings for a single statement.
    /// </summary>
    public static List<PlanWarning> AllNodeWarnings(PlanStatement stmt)
    {
        var warnings = new List<PlanWarning>();
        if (stmt.RootNode != null)
            CollectNodeWarnings(stmt.RootNode, warnings);
        return warnings;
    }

    private static void CollectNodeWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }
}
