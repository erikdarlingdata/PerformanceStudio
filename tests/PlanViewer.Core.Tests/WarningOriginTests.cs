using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #440: a warning did not record which operator it came from, so on a large plan there was no way
/// to get from a finding to the thing that produced it.
///
/// The interesting half of this feature is knowing when to say nothing. A warning pointed at the
/// wrong operator is worse than a warning pointed nowhere, because the reader would believe it — so
/// these pin both directions: that origins appear where a rule genuinely knows, and that they stay
/// empty where no operator is responsible.
/// </summary>
public class WarningOriginTests
{
    /// <summary>
    /// Operator warnings are stamped in one place, at the end of AnalyzeNode, rather than at the 26
    /// separate sites that add one. This asserts the property that arrangement buys: across every
    /// committed plan, no operator warning is ever left without an origin.
    /// </summary>
    [Fact]
    public void EveryOperatorWarningKnowsItsOperator()
    {
        var plansDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Plans");
        var orphans = new System.Collections.Generic.List<string>();

        foreach (var file in System.IO.Directory.GetFiles(plansDir, "*.sqlplan"))
        {
            var plan = PlanTestHelper.LoadAndAnalyze(System.IO.Path.GetFileName(file));
            foreach (var stmt in plan.Batches.SelectMany(b => b.Statements))
            {
                if (stmt.RootNode == null) continue;
                foreach (var (node, warning) in Walk(stmt.RootNode))
                {
                    if (warning.OriginNodeIds.Count == 0)
                        orphans.Add($"{System.IO.Path.GetFileName(file)}:{warning.WarningType}");
                    else if (!warning.OriginNodeIds.Contains(node.NodeId))
                        orphans.Add($"{System.IO.Path.GetFileName(file)}:{warning.WarningType} points away from its own node");
                }
            }
        }

        Assert.True(orphans.Count == 0, "Operator warnings without a usable origin: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// The statement-level table variable warning is the case that motivated carrying origins on
    /// statement warnings at all: the rule already walked the tree and knew exactly which operators
    /// touched a table variable, and threw that away before emitting.
    /// </summary>
    [Fact]
    public void TheTableVariableWarningPointsAtTheOperatorsThatTouchOne()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("table_variable_plan.sqlplan");
        var statementWarnings = plan.Batches
            .SelectMany(b => b.Statements)
            .SelectMany(s => s.PlanWarnings)
            .Where(w => w.WarningType == "Table Variable")
            .ToList();

        Assert.NotEmpty(statementWarnings);
        Assert.All(statementWarnings, w => Assert.NotEmpty(w.OriginNodeIds));
    }

    /// <summary>
    /// The other direction, and the one worth protecting. "High Compile CPU" is measured before a
    /// single row is read, so no operator is responsible for it; SQL Server reports "UDF Execution"
    /// at the statement level only. Both must stay empty so the UI offers no link rather than a
    /// misleading one.
    /// </summary>
    [Theory]
    [InlineData("convert_implicit_plan.sqlplan", "High Compile CPU")]
    [InlineData("udf_plan.sqlplan", "UDF Execution")]
    public void WarningsWithNoResponsibleOperatorClaimNone(string planFile, string warningType)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(planFile);
        var matching = plan.Batches
            .SelectMany(b => b.Statements)
            .SelectMany(s => s.PlanWarnings)
            .Where(w => w.WarningType == warningType)
            .ToList();

        Assert.NotEmpty(matching);
        Assert.All(matching, w => Assert.Empty(w.OriginNodeIds));
    }

    /// <summary>The JSON consumers get origins as a field, not by re-deriving them from the tree.</summary>
    [Fact]
    public void TheJsonOutputCarriesOrigins()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("table_variable_plan.sqlplan");
        var result = ResultMapper.Map(plan, "table_variable_plan.sqlplan");

        var tableVariable = result.Statements
            .SelectMany(s => s.Warnings)
            .Single(w => w.Type == "Table Variable");

        Assert.NotEmpty(tableVariable.OriginNodeIds);
    }

    /// <summary>
    /// The index the statement panel is built from. The reporter's case is a plan too big to hunt
    /// through by hand, so "found all of them" is the property that matters — this compares the
    /// index against an independent recursive walk rather than against a hand-written expected
    /// count, so it cannot drift with the fixtures.
    /// </summary>
    [Fact]
    public void TheOperatorWarningIndexFindsEveryWarningInTheTree()
    {
        var plansDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Plans");
        var mismatches = new System.Collections.Generic.List<string>();

        foreach (var file in System.IO.Directory.GetFiles(plansDir, "*.sqlplan"))
        {
            var plan = PlanTestHelper.LoadAndAnalyze(System.IO.Path.GetFileName(file));
            foreach (var stmt in plan.Batches.SelectMany(b => b.Statements))
            {
                if (stmt.RootNode == null) continue;
                var indexed = PlanViewer.Core.Services.WarningIndex.CollectOperatorWarnings(stmt.RootNode).Count;
                var walked = Walk(stmt.RootNode).Count();
                if (indexed != walked)
                    mismatches.Add($"{System.IO.Path.GetFileName(file)}: indexed {indexed} vs walked {walked}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(", ", mismatches));
    }

    /// <summary>A statement with no operator tree indexes to nothing rather than throwing.</summary>
    [Fact]
    public void TheOperatorWarningIndexHandlesAMissingTree()
    {
        Assert.Empty(PlanViewer.Core.Services.WarningIndex.CollectOperatorWarnings(null));
    }

    private static System.Collections.Generic.IEnumerable<(PlanNode Node, PlanWarning Warning)> Walk(PlanNode node)
    {
        foreach (var w in node.Warnings) yield return (node, w);
        foreach (var child in node.Children)
            foreach (var pair in Walk(child)) yield return pair;
    }
}
