using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #575: a severity override reaches every finding its rule emits. Overrides used to find a
/// finding's rule by matching its type against a rule-to-name table, and a finding the table had
/// no entry for (rules 34-37 and 39, rule 30's Low Impact Index and Duplicate Index Suggestions,
/// rule 10's RID Lookup) kept its default severity with no error.
/// </summary>
public class SeverityOverrideTests
{
    // More numbers than the analyzer has rules, so a new rule is covered without an edit here.
    private static readonly int[] EveryRuleNumber = Enumerable.Range(1, 99).ToArray();

    private static AnalyzerConfig Overrides(IEnumerable<int> rules, PlanWarningSeverity severity) =>
        new()
        {
            Rules = new RulesConfig
            {
                SeverityOverrides = rules.ToDictionary(r => r, _ => severity.ToString())
            }
        };

    private static AnalyzerConfig Disabled(int rule) =>
        new() { Rules = new RulesConfig { Disabled = [rule] } };

    private static List<PlanWarning> Analyze(PlanStatement stmt, AnalyzerConfig? config)
    {
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
        PlanAnalyzer.Analyze(plan, config);
        return PlanTestHelper.AllWarnings(plan);
    }

    /// <summary>
    /// Parse and analyze only. The scorer's wait-stats findings come from no rule, so they would
    /// only get in the way here.
    /// </summary>
    private static List<PlanWarning> AnalyzeFixture(string fileName, AnalyzerConfig? config)
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", fileName))
            .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalyzer.Analyze(plan, config);
        return PlanTestHelper.AllWarnings(plan);
    }

    [Fact]
    public void IssueRepro_Rule36AndRule30OverridesApply()
    {
        var stmt = new PlanStatement
        {
            CursorActualType = "Dynamic",
            MissingIndexes = [new MissingIndex { Table = "T", Impact = 10 }]
        };
        var warnings = Analyze(stmt, new AnalyzerConfig
        {
            Rules = new RulesConfig { SeverityOverrides = { [36] = "Info", [30] = "Critical" } }
        });

        Assert.Equal(PlanWarningSeverity.Info,
            Assert.Single(warnings, w => w.WarningType == "Dynamic Cursor").Severity);
        Assert.Equal(PlanWarningSeverity.Critical,
            Assert.Single(warnings, w => w.WarningType == "Low Impact Index").Severity);
    }

    private static MissingIndex Suggestion(double impact = 90, int includes = 0) => new()
    {
        Database = "D",
        Schema = "dbo",
        Table = "T",
        Impact = impact,
        EqualityColumns = ["a"],
        IncludeColumns = Enumerable.Range(1, includes).Select(i => $"c{i}").ToList()
    };

    private static PlanStatement StatementThatEmits(string warningType) => warningType switch
    {
        "Dynamic Cursor" => new PlanStatement { CursorActualType = "Dynamic" },
        "Cursor Missing LOCAL" => new PlanStatement { StatementText = "DECLARE c CURSOR FOR SELECT a FROM dbo.t" },
        "Truncated Query Text" => new PlanStatement
        {
            StatementText = "SELECT " + new string('a', PlanStatement.TruncationLengthThreshold)
        },
        "Low Impact Index" => new PlanStatement { MissingIndexes = [Suggestion(impact: 10)] },
        "Wide Index Suggestion" => new PlanStatement { MissingIndexes = [Suggestion(includes: 6)] },
        "Duplicate Index Suggestions" => new PlanStatement { MissingIndexes = [Suggestion(), Suggestion()] },
        _ => throw new ArgumentOutOfRangeException(nameof(warningType), warningType, null)
    };

    [Theory]
    [InlineData("Dynamic Cursor", 36)]
    [InlineData("Cursor Missing LOCAL", 37)]
    [InlineData("Truncated Query Text", 39)]
    [InlineData("Low Impact Index", 30)]
    [InlineData("Wide Index Suggestion", 30)]
    [InlineData("Duplicate Index Suggestions", 30)]
    public void StatementFindingTakesItsRulesOverride(string warningType, int rule) =>
        AssertOverrideReaches(config => Analyze(StatementThatEmits(warningType), config), warningType, rule);

    [Theory]
    [InlineData("rid_lookup_plan.sqlplan", "RID Lookup", 10)]
    [InlineData("cte_multi_ref_plan.sqlplan", "Bare Scan", 34)]
    [InlineData("excellent-parallel-spill.sqlplan", "Expensive Operator", 35)]
    public void OperatorFindingTakesItsRulesOverride(string fixture, string warningType, int rule) =>
        AssertOverrideReaches(config => AnalyzeFixture(fixture, config), warningType, rule);

    private static void AssertOverrideReaches(
        Func<AnalyzerConfig?, List<PlanWarning>> analyze, string warningType, int rule)
    {
        var byDefault = analyze(null).Where(w => w.WarningType == warningType).ToList();
        Assert.NotEmpty(byDefault);

        // A severity the rule did not give at least one of them, so it can only come from the override.
        var target = byDefault.Any(w => w.Severity != PlanWarningSeverity.Info)
            ? PlanWarningSeverity.Info
            : PlanWarningSeverity.Critical;
        var overridden = analyze(Overrides([rule], target)).Where(w => w.WarningType == warningType).ToList();

        Assert.Equal(byDefault.Count, overridden.Count);
        Assert.All(overridden, w => Assert.Equal(target, w.Severity));
    }

    public static TheoryData<string> Fixtures() =>
        new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Plans"), "*.sqlplan")
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal));

    /// <summary>
    /// Over the whole plan corpus: every finding the analyzer makes names the rule that made it,
    /// that rule's switch is the one that turns it off, and that rule's override sets its
    /// severity. The engine's own warnings name no rule and keep their severity whatever is
    /// overridden, as before #575.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryAnalyzerFindingFollowsItsRule(string fixture)
    {
        var byDefault = AnalyzeFixture(fixture, null);
        var ours = byDefault.Where(w => w.Source == PlanWarningSource.PerformanceStudio).ToList();

        Assert.All(ours, w => Assert.True(w.RuleNumber.HasValue, $"{w.WarningType} names no rule"));
        Assert.All(byDefault.Where(w => w.Source == PlanWarningSource.SqlServer),
            w => Assert.Null(w.RuleNumber));

        foreach (var rule in ours.Select(w => w.RuleNumber!.Value).Distinct())
            Assert.DoesNotContain(AnalyzeFixture(fixture, Disabled(rule)), w => w.RuleNumber == rule);

        foreach (var target in new[] { PlanWarningSeverity.Info, PlanWarningSeverity.Critical })
        {
            var overridden = AnalyzeFixture(fixture, Overrides(EveryRuleNumber, target));

            // An override changes severity only, so both runs list the same findings in the same order.
            Assert.Equal(byDefault.Select(w => w.WarningType), overridden.Select(w => w.WarningType));
            for (var i = 0; i < byDefault.Count; i++)
            {
                var expected = byDefault[i].Source == PlanWarningSource.SqlServer
                    ? byDefault[i].Severity
                    : target;
                Assert.Equal(expected, overridden[i].Severity);
            }
        }
    }
}
