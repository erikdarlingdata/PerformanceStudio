using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PlanViewer.App.Mcp;
using AnalyzerConfig = PlanViewer.Core.Models.AnalyzerConfig;
using CorePlanSession = PlanViewer.Core.Models.PlanSession;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #651: a suppression in .planview.json marks one finding on one statement as accepted. The
/// finding stays in the output, flagged with its reason, and drops out of the summary counts.
/// </summary>
public class SuppressionTests
{
    private const string Hash = "0x00E1987CD1C28B4C";

    private static PlanWarning Warn(
        string type,
        int? rule,
        PlanWarningSeverity severity = PlanWarningSeverity.Warning,
        PlanWarningSource source = PlanWarningSource.PerformanceStudio) =>
        new()
        {
            WarningType = type,
            Message = type + " message",
            Severity = severity,
            RuleNumber = rule,
            Source = source
        };

    private static PlanNode Node(int id, string physicalOp, string? objectName, params PlanWarning[] warnings) =>
        new()
        {
            NodeId = id,
            PhysicalOp = physicalOp,
            ObjectName = objectName,
            Warnings = warnings.ToList()
        };

    /// <summary>
    /// One statement. The statement carries a rule finding and an engine warning. The tree is a
    /// Nested Loops (node 0) over two Key Lookups of the same type, on different tables (nodes 1
    /// and 2). Node 2 also carries the engine warning.
    /// </summary>
    private static ParsedPlan SamplePlan(string hash = Hash)
    {
        var root = Node(0, "Nested Loops", null);
        root.Children.Add(Node(1, "Key Lookup", "eventuous.Messages", Warn("Key Lookup", 10)));
        root.Children.Add(Node(2, "Key Lookup", "dbo.Orders",
            Warn("Key Lookup", 10, PlanWarningSeverity.Critical),
            Warn("Implicit Conversion", null, source: PlanWarningSource.SqlServer)));

        var stmt = new PlanStatement
        {
            QueryHash = hash,
            RootNode = root,
            PlanWarnings =
            [
                Warn("Dynamic Cursor", 36),
                Warn("Implicit Conversion", null, source: PlanWarningSource.SqlServer)
            ]
        };
        return new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
    }

    private static AnalyzerConfig Cfg(params SuppressionEntry[] entries) =>
        new() { Suppressions = entries.ToList() };

    private static SuppressionEntry Entry(
        string hash = Hash, int? rule = null, string? type = null, string? op = null, string reason = "accepted") =>
        new() { QueryHash = hash, Rule = rule, Type = type, Operator = op, Reason = reason };

    private static ParsedPlan Suppress(ParsedPlan plan, params SuppressionEntry[] entries)
    {
        SuppressionApplier.Apply(plan, Cfg(entries));
        return plan;
    }

    private static List<PlanWarning> Suppressed(ParsedPlan plan) =>
        PlanTestHelper.AllWarnings(plan).Where(w => w.IsSuppressed).ToList();

    [Fact]
    public void RuleMatchesEveryFindingOfThatRuleOnTheStatement()
    {
        var plan = Suppress(SamplePlan(), Entry(rule: 10));

        var suppressed = Suppressed(plan);
        Assert.Equal(2, suppressed.Count);
        Assert.All(suppressed, w => Assert.Equal(10, w.RuleNumber));
        Assert.All(suppressed, w => Assert.Equal("accepted", w.SuppressionReason));
    }

    [Fact]
    public void RuleNeverMatchesAnEngineWarning()
    {
        var plan = Suppress(SamplePlan(), Entry(rule: 10));

        var engine = PlanTestHelper.AllWarnings(plan).Where(w => w.Source == PlanWarningSource.SqlServer);
        Assert.All(engine, w => Assert.False(w.IsSuppressed));
    }

    [Fact]
    public void TypeMatchesAWarningThatHasNoRuleNumber()
    {
        var plan = Suppress(SamplePlan(), Entry(type: "implicit conversion"));

        var suppressed = Suppressed(plan);
        Assert.Equal(2, suppressed.Count);
        Assert.All(suppressed, w => Assert.Null(w.RuleNumber));
        Assert.All(suppressed, w => Assert.Equal("Implicit Conversion", w.WarningType));
    }

    [Fact]
    public void RuleAndTypeBothHaveToMatch()
    {
        var wrongType = Suppress(SamplePlan(), Entry(rule: 10, type: "Dynamic Cursor"));
        Assert.Empty(Suppressed(wrongType));

        var rightType = Suppress(SamplePlan(), Entry(rule: 10, type: "Key Lookup"));
        Assert.Equal(2, Suppressed(rightType).Count);
    }

    [Theory]
    [InlineData("0x00E1987CD1C28B4C")]
    [InlineData("0X00E1987CD1C28B4C")]
    [InlineData("00E1987CD1C28B4C")]
    [InlineData("0x00e1987cd1c28b4c")]
    [InlineData("00e1987cd1c28b4c")]
    [InlineData("  0x00e1987cd1c28b4c  ")]
    public void HashMatchesWithOrWithoutThePrefixInEitherCase(string configured)
    {
        var plan = Suppress(SamplePlan("0x00E1987CD1C28B4C"), Entry(hash: configured, rule: 36));
        Assert.Single(Suppressed(plan));
    }

    [Theory]
    [InlineData("00e1987cd1c28b4c")]
    [InlineData("0X00E1987CD1C28B4C")]
    public void HashMatchesWhenThePlanSideDiffersInShape(string planHash)
    {
        var plan = Suppress(SamplePlan(planHash), Entry(hash: "0x00E1987CD1C28B4C", rule: 36));
        Assert.Single(Suppressed(plan));
    }

    [Fact]
    public void AnotherStatementsHashIsLeftAlone()
    {
        var plan = Suppress(SamplePlan("0x1111111111111111"), Entry(rule: 10));
        Assert.Empty(Suppressed(plan));
    }

    [Fact]
    public void AStatementWithNoHashIsNeverMatched()
    {
        var plan = Suppress(SamplePlan(hash: null!), Entry(rule: 10));
        Assert.Empty(Suppressed(plan));
    }

    [Fact]
    public void OperatorMatchesOnlyTheNodeWhoseLabelContainsIt()
    {
        var plan = Suppress(SamplePlan(), Entry(rule: 10, op: "eventuous.Messages"));

        var suppressed = Assert.Single(Suppressed(plan));
        var root = plan.Batches[0].Statements[0].RootNode!;
        Assert.Same(suppressed, root.Children[0].Warnings.Single());
        // Node 2 holds the same type of finding and is untouched.
        Assert.False(root.Children[1].Warnings.First(w => w.RuleNumber == 10).IsSuppressed);
    }

    [Fact]
    public void OperatorIsMatchedAgainstTheLabelTheJsonOutputPrints()
    {
        var plan = SamplePlan();
        var result = ResultMapper.Map(plan, "x");
        var printed = result.Statements[0].OperatorTree!.Children[0].Warnings.Single().Operator!;

        // Pasting the printed label into a suppression has to hit that node and no other.
        Suppress(plan, Entry(rule: 10, op: printed));

        Assert.Single(Suppressed(plan));
        Assert.Equal("Key Lookup on eventuous.Messages (Node 1)", printed);
    }

    [Fact]
    public void OperatorIsCaseInsensitive()
    {
        var plan = Suppress(SamplePlan(), Entry(rule: 10, op: "DBO.ORDERS"));
        Assert.Single(Suppressed(plan));
    }

    [Fact]
    public void OperatorNeverMatchesAStatementLevelWarning()
    {
        var plan = Suppress(SamplePlan(), Entry(rule: 36, op: "Nested Loops"));
        Assert.Empty(Suppressed(plan));

        var withoutOperator = Suppress(SamplePlan(), Entry(rule: 36));
        Assert.Single(Suppressed(withoutOperator));
    }

    [Fact]
    public void ASuppressionKeepsTheWarningAndItsSeverity()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(rule: 10, op: "dbo.Orders"));

        var warning = plan.Batches[0].Statements[0].RootNode!.Children[1].Warnings.First(w => w.RuleNumber == 10);
        Assert.True(warning.IsSuppressed);
        Assert.Equal(PlanWarningSeverity.Critical, warning.Severity);
        Assert.Equal(5, PlanTestHelper.AllWarnings(plan).Count);
    }

    [Fact]
    public void ASeverityOverrideAndASuppressionBothApplyToOneWarning()
    {
        var stmt = new PlanStatement
        {
            QueryHash = Hash,
            CursorActualType = "Dynamic"
        };
        var plan = new ParsedPlan { Batches = [new PlanBatch { Statements = [stmt] }] };
        var config = new AnalyzerConfig
        {
            Rules = new RulesConfig { SeverityOverrides = { [36] = "Info" } },
            Suppressions = [Entry(rule: 36, reason: "vendor cursor")]
        };

        PlanAnalysisPipeline.AnalyzeParsed(plan, config, cancellationToken: TestContext.Current.CancellationToken);

        var warning = Assert.Single(stmt.PlanWarnings, w => w.RuleNumber == 36);
        Assert.Equal(PlanWarningSeverity.Info, warning.Severity);
        Assert.True(warning.IsSuppressed);
        Assert.Equal("vendor cursor", warning.SuppressionReason);
    }

    [Fact]
    public void ASuppressionReachesAStatementInsideAProcedureBody()
    {
        var baseline = PlanTestHelper.LoadAndAnalyze("exec_stored_procedure_plan.sqlplan");
        var topLevel = baseline.Batches.SelectMany(b => b.Statements).ToHashSet();
        var nested = PlanStatements.EnumerateAll(baseline)
            .Where(s => !topLevel.Contains(s) && !string.IsNullOrEmpty(s.QueryHash))
            .Select(s => (Statement: s, Warning: AllOf(s).FirstOrDefault(w => w.RuleNumber != null)))
            .First(x => x.Warning != null);

        var hash = nested.Statement.QueryHash!;
        var rule = nested.Warning!.RuleNumber!.Value;

        var xml = File.ReadAllText(Path.Combine("Plans", "exec_stored_procedure_plan.sqlplan"))
            .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
        var plan = ShowPlanParser.Parse(xml);
        PlanAnalysisPipeline.AnalyzeParsed(plan, Cfg(Entry(hash: hash, rule: rule, reason: "library owns it")),
            cancellationToken: TestContext.Current.CancellationToken);

        var suppressedStatements = PlanStatements.EnumerateAll(plan)
            .Where(s => AllOf(s).Any(w => w.IsSuppressed))
            .ToList();
        Assert.NotEmpty(suppressedStatements);
        Assert.All(suppressedStatements, s => Assert.Equal(hash, s.QueryHash));
        Assert.DoesNotContain(plan.Batches.SelectMany(b => b.Statements),
            s => AllOf(s).Any(w => w.IsSuppressed) && s.QueryHash != hash);
        Assert.All(PlanTestHelper.AllWarnings(plan).Where(w => w.IsSuppressed),
            w => Assert.Equal(rule, w.RuleNumber));
    }

    private static List<PlanWarning> AllOf(PlanStatement stmt)
    {
        var all = new List<PlanWarning>(stmt.PlanWarnings);
        all.AddRange(PlanTestHelper.AllNodeWarnings(stmt));
        return all;
    }

    [Fact]
    public void AScorerWaitStatsFindingCanBeSuppressedByType()
    {
        // The scorer adds its wait-stats findings after the analyzer, so the pass has to run last.
        static ParsedPlan Run(AnalyzerConfig config)
        {
            var xml = File.ReadAllText(Path.Combine("Plans", "case_predicate_plan.sqlplan"))
                .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
            var parsed = ShowPlanParser.Parse(xml);
            PlanAnalysisPipeline.AnalyzeParsed(parsed, config, cancellationToken: TestContext.Current.CancellationToken);
            return parsed;
        }

        var baseline = Run(new AnalyzerConfig());
        var wait = PlanStatements.EnumerateAll(baseline)
            .SelectMany(s => s.PlanWarnings.Select(w => (Statement: s, Warning: w)))
            .First(x => x.Warning.WarningType.StartsWith("Wait: ", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(x.Statement.QueryHash));
        Assert.Null(wait.Warning.RuleNumber);

        var plan = Run(Cfg(Entry(hash: wait.Statement.QueryHash!, type: wait.Warning.WarningType)));

        Assert.Contains(PlanTestHelper.AllWarnings(plan),
            w => w.IsSuppressed && w.WarningType == wait.Warning.WarningType);
    }

    // ---- Output -------------------------------------------------------------------------------

    [Fact]
    public void SuppressedWarningsLeaveTheSummaryCountsButStayInTheOutput()
    {
        var plan = SamplePlan();
        var before = ResultMapper.Map(plan, "x").Summary;
        Assert.Equal(5, before.TotalWarnings);
        Assert.Equal(1, before.CriticalWarnings);
        Assert.Equal(0, before.SuppressedWarnings);

        // The Critical lookup on dbo.Orders, and the statement-level Dynamic Cursor.
        Suppress(plan, Entry(rule: 10, op: "dbo.Orders", reason: "r1"), Entry(rule: 36, reason: "r2"));
        var result = ResultMapper.Map(plan, "x");

        Assert.Equal(3, result.Summary.TotalWarnings);
        Assert.Equal(0, result.Summary.CriticalWarnings);
        Assert.Equal(2, result.Summary.SuppressedWarnings);
        Assert.DoesNotContain("Dynamic Cursor", result.Summary.WarningTypes);
        Assert.Contains("Key Lookup", result.Summary.WarningTypes); // node 1 is still active

        var statement = result.Statements[0];
        var cursor = Assert.Single(statement.Warnings, w => w.Type == "Dynamic Cursor");
        Assert.True(cursor.IsSuppressed);
        Assert.Equal("r2", cursor.SuppressionReason);

        var lookup = statement.OperatorTree!.Children[1].Warnings.First(w => w.Type == "Key Lookup");
        Assert.True(lookup.IsSuppressed);
        Assert.Equal("r1", lookup.SuppressionReason);
        Assert.Equal("Critical", lookup.Severity);
    }

    [Fact]
    public void WarningTypesDropsATypeOnlyWhenEveryFindingOfItIsSuppressed()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(type: "Key Lookup"));

        var summary = ResultMapper.Map(plan, "x").Summary;

        Assert.DoesNotContain("Key Lookup", summary.WarningTypes);
        Assert.Equal(3, summary.TotalWarnings);
        Assert.Equal(2, summary.SuppressedWarnings);
    }

    [Fact]
    public void TheSerializedJsonCarriesRuleSuppressedAndReasonAndOmitsRuleForEngineWarnings()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(rule: 10, op: "eventuous.Messages", reason: "library owns it"));

        var json = JsonSerializer.Serialize(ResultMapper.Map(plan, "x"), AnalysisJson.IndentedWithoutNulls);
        using var doc = JsonDocument.Parse(json);
        var statement = doc.RootElement.GetProperty("statements")[0];

        Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("suppressed_warnings").GetInt32());

        var cursor = statement.GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("type").GetString() == "Dynamic Cursor");
        Assert.Equal(36, cursor.GetProperty("rule").GetInt32());
        Assert.False(cursor.GetProperty("suppressed").GetBoolean());
        Assert.False(cursor.TryGetProperty("suppression_reason", out _));

        var engine = statement.GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("type").GetString() == "Implicit Conversion");
        Assert.False(engine.TryGetProperty("rule", out _));
        Assert.False(engine.GetProperty("suppressed").GetBoolean());

        var lookup = statement.GetProperty("operator_tree").GetProperty("children")[0].GetProperty("warnings")[0];
        Assert.Equal(10, lookup.GetProperty("rule").GetInt32());
        Assert.True(lookup.GetProperty("suppressed").GetBoolean());
        Assert.Equal("library owns it", lookup.GetProperty("suppression_reason").GetString());
    }

    [Fact]
    public void TheTextOutputTagsASuppressedWarningWithItsReasonAndReportsTheCount()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(rule: 36, reason: "vendor cursor"));

        var text = TextFormatter.Format(ResultMapper.Map(plan, "x"));

        Assert.Contains("[suppressed: vendor cursor]", text);
        Assert.Contains("Suppressed warnings: 1", text);
        Assert.Contains("Warnings: 4 (1 critical)", text);
    }

    [Fact]
    public void TheTextOutputTagsSuppressedOperatorWarningsOnTheGroupedAndTheSinglePath()
    {
        // Two warnings that share a severity and a harm sentence are written as a group; the
        // third has no harm sentence and is written on its own. Both paths build their own tag.
        static PlanWarning Estimate(string data) => new()
        {
            WarningType = "Row Estimate Mismatch",
            Message = data + ". The underestimate caused a spill.",
            Severity = PlanWarningSeverity.Warning,
            RuleNumber = 5
        };

        var root = Node(0, "Nested Loops", null);
        root.Children.Add(Node(1, "Index Scan", "dbo.A", Estimate("Estimated 1 row, got 900")));
        root.Children.Add(Node(2, "Index Scan", "dbo.B", Estimate("Estimated 2 rows, got 800")));
        root.Children.Add(Node(3, "Index Scan", "dbo.C",
            new PlanWarning { WarningType = "Scan With Predicate", Message = "Scan reads every row", Severity = PlanWarningSeverity.Info, RuleNumber = 6 }));
        var plan = new ParsedPlan
        {
            Batches = [new PlanBatch { Statements = [new PlanStatement { QueryHash = Hash, RootNode = root }] }]
        };
        Suppress(plan,
            Entry(rule: 5, op: "dbo.A", reason: "grouped reason"),
            Entry(rule: 6, reason: "single reason"));

        var lines = TextFormatter.Format(ResultMapper.Map(plan, "x")).Split('\n');

        var groupedA = Assert.Single(lines, l => l.Contains("dbo.A"));
        var groupedB = Assert.Single(lines, l => l.Contains("dbo.B"));
        var single = Assert.Single(lines, l => l.Contains("dbo.C"));
        Assert.Contains("[suppressed: grouped reason]", groupedA);
        Assert.DoesNotContain("[suppressed", groupedB);
        Assert.Contains("[suppressed: single reason]", single);
    }

    [Fact]
    public void TheHtmlOutputTagsASuppressedWarningWithItsReason()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(rule: 36, reason: "vendor <cursor>"));

        var html = HtmlExporter.Export(ResultMapper.Map(plan, "x"), "");

        Assert.Contains("suppressed: vendor &lt;cursor&gt;", html);
    }

    [Fact]
    public void TheMcpSummaryAndWarningListCarryTheSuppression()
    {
        var plan = SamplePlan();
        Suppress(plan, Entry(rule: 36, reason: "vendor cursor"));

        var manager = new PlanSessionManager();
        var sessionId = $"mcp-{Guid.NewGuid():N}";
        manager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = "sample",
            Source = "file",
            Plan = plan
        });
        var operations = new PlanOperations(manager, AnalyzerConfig.Default, enforceQueryAdmission: false);

        var summary = operations.GetSummary(sessionId);
        Assert.Equal(1, summary.SuppressedWarnings);
        Assert.Equal(4, summary.TotalWarnings);

        var warnings = operations.GetWarnings(
            sessionId, null, TestContext.Current.CancellationToken);
        var cursor = Assert.Single(warnings.Warnings, w => w.Type == "Dynamic Cursor");
        Assert.True(cursor.IsSuppressed);
        Assert.Equal("vendor cursor", cursor.SuppressionReason);
        Assert.Equal(36, cursor.Rule);
        var engine = warnings.Warnings.First(w => w.Type == "Implicit Conversion");
        Assert.Null(engine.Rule);
        Assert.False(engine.IsSuppressed);
    }
}
