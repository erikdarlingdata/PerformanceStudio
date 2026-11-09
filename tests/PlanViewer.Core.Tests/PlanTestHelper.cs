using System;
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
    /// Same load + analyze + score, but overrides every statement's QueryTimeStats.ElapsedTimeMs
    /// before analysis runs. Used to test elapsed-time thresholds (e.g. rule 35's #562 floor) on
    /// either side of the line without needing a fixture captured at an exact millisecond value.
    /// </summary>
    public static ParsedPlan LoadAndAnalyzeWithElapsedTimeMs(string planFileName, long elapsedTimeMs)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);

        foreach (var stmt in PlanStatements.EnumerateAll(plan))
            if (stmt.QueryTimeStats != null)
                stmt.QueryTimeStats.ElapsedTimeMs = elapsedTimeMs;

        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Same load + analyze + score, with every MemoryGrantInfo's GrantWaitTime attribute rewritten to
    /// <paramref name="seconds"/> before parsing. The showplan records that attribute in seconds, so
    /// a test can put a wait on either side of the analyzer's millisecond cut without a fixture per
    /// value. Fails if the fixture carries no GrantWaitTime to rewrite.
    /// </summary>
    public static ParsedPlan LoadAndAnalyzeWithGrantWaitSeconds(string planFileName, int seconds)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var pattern = new System.Text.RegularExpressions.Regex("GrantWaitTime=\"\\d+\"");
        Assert.True(pattern.IsMatch(xml), $"{planFileName} has no GrantWaitTime attribute to rewrite");
        xml = pattern.Replace(xml, $"GrantWaitTime=\"{seconds}\"");

        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Same load + analyze + score, with the scan at each node id lifted past rule 11's actual-plan
    /// gates before analysis: at least 10,000 rows read, and the statement's elapsed time as the scan's
    /// own time when the plan carries statement time. For tests whose fixture is a small repro table
    /// and whose subject is another rule's decision about the same scan, proven by rule 11 reporting
    /// the predicate as a plain residual one.
    /// </summary>
    public static ParsedPlan LoadAndAnalyzeWithReportableScan(string planFileName, params int[] nodeIds)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);

        var stmt = FirstStatement(plan);
        Assert.NotNull(stmt.RootNode);
        foreach (var nodeId in nodeIds)
        {
            var node = FindNode(stmt.RootNode, nodeId);
            Assert.NotNull(node);
            node.ActualRowsRead = Math.Max(node.ActualRowsRead, 10_000);
            node.HasActualRowsRead = true;
            if (stmt.QueryTimeStats is { ElapsedTimeMs: > 0 } time)
                node.ActualElapsedMs = time.ElapsedTimeMs;
        }

        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Same load + analyze + score, with an analyzer config (for rules-config behavior like
    /// severity overrides). Named rather than overloaded so an existing
    /// <c>LoadAndAnalyze(name, null)</c> call can never silently pick a different overload.
    /// </summary>
    public static ParsedPlan LoadAndAnalyzeWithConfig(string planFileName, AnalyzerConfig config)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");

        var xml = File.ReadAllText(path);
        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan, config);
        BenefitScorer.Score(plan);
        return plan;
    }

    /// <summary>
    /// Gets all warnings across all statements and all nodes in the plan.
    /// </summary>
    public static List<PlanWarning> AllWarnings(ParsedPlan plan)
    {
        var warnings = new List<PlanWarning>();

        /* #455: every statement, including the ones inside a stored procedure or UDF body. This
           walked batch.Statements alone, exactly like the analyzer did, so the golden master could
           not have caught the analyzer skipping procedure bodies - the two shared a blind spot. */
        foreach (var stmt in PlanViewer.Core.Services.PlanStatements.EnumerateAll(plan))
        {
            warnings.AddRange(stmt.PlanWarnings);

            if (stmt.RootNode != null)
                CollectNodeWarnings(stmt.RootNode, warnings);
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
        // Walk up from bin/Debug/net10.0 to find the repo root
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
