using System;
using System.IO;
using System.Text.Json;
using PlanViewer.App.Mcp;
using AnalyzerConfig = PlanViewer.Core.Models.AnalyzerConfig;
using CorePlanSession = PlanViewer.Core.Models.PlanSession;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #482: #467 put the parameter values back at the two copy paths it was looking at, and the
/// reporter came straight back with "still shows parametrized in Human and Robot Advice". Both
/// advice buttons — and the HTML export, the comparison report, and every MCP tool — read their
/// statement text out of <see cref="ResultMapper"/>, so that is the seam these cover.
///
/// <para>The plan is the #466 reproduction: seven parameters the engine manufactured under
/// <c>PARAMETERIZATION FORCED</c>, six of them carrying a runtime value and the seventh only a
/// compiled one.</para>
/// </summary>
public class AnalysisParameterSubstitutionTests
{
    private const string ParameterizedPlan = "forced_parameterization_plan.sqlplan";
    private const string NamedParameterPlan = "local_variable_plan.sqlplan";

    /* Two of the seven. The first is a string that keeps its quotes, the second a numeric IN list
       whose values arrive from showplan wrapped in parentheses. */
    private const string SubstitutedPredicate = "[t0].[AuthUserId]='123456'";
    private const string ParameterizedPredicate = "[t0].[AuthUserId]=@0";
    private const string SubstitutedInList = "[t].[StatusId] in (5,6,7,8,9)";

    [Fact]
    public void AdviceForHumans_ShowsTheValuesInsteadOfTheParameterNames()
    {
        var text = TextFormatter.Format(Analyze(ParameterizedPlan));

        Assert.Contains(SubstitutedPredicate, text);
        Assert.Contains(SubstitutedInList, text);
        Assert.Contains("[t].[FromDateTime]>='2026-05-28 10:28:07.3132561'", text);

        /* Scoped to the predicate rather than a bare "@0" — the Parameters section below the
           statement lists the names on purpose, and should keep doing so. */
        Assert.DoesNotContain(ParameterizedPredicate, text);
    }

    [Fact]
    public void AdviceForRobots_ShowsTheValuesAndStillCarriesTheParameterizedForm()
    {
        var json = JsonSerializer.Serialize(Analyze(ParameterizedPlan), AnalysisJson.Indented);

        using var document = JsonDocument.Parse(json);
        var statement = document.RootElement.GetProperty("statements")[0];
        var runnable = statement.GetProperty("statement_text").GetString();
        var parameterized = statement.GetProperty("parameterized_statement_text").GetString();

        Assert.NotNull(runnable);
        Assert.NotNull(parameterized);

        Assert.Contains(SubstitutedPredicate, runnable);
        Assert.DoesNotContain(ParameterizedPredicate, runnable);

        /* Both forms, and they say different things — the parameterized one is what matches the
           plan cache and Query Store, and throwing it away to fix the advice would have been a
           trade nobody asked for. */
        Assert.Contains(ParameterizedPredicate, parameterized);
        Assert.NotEqual(runnable, parameterized);
    }

    [Fact]
    public void HtmlExport_ShowsTheValues()
    {
        var analysis = Analyze(ParameterizedPlan);

        var html = HtmlExporter.Export(analysis, TextFormatter.Format(analysis));

        /* The IN list rather than the string predicate: the export HTML-encodes what it writes, and
           an assertion carrying quotes would be testing HttpUtility rather than this change. */
        Assert.Contains(SubstitutedInList, html);
        Assert.DoesNotContain("[t].[StatusId] in (@1,@2,@3,@4,@5)", html);
    }

    [Fact]
    public void ComparisonReport_ShowsTheValues()
    {
        var analysis = Analyze(ParameterizedPlan);

        var comparison = ComparisonFormatter.Compare(analysis, analysis, "before", "after");

        Assert.Contains(SubstitutedPredicate, comparison);
        Assert.DoesNotContain(ParameterizedPredicate, comparison);
    }

    [Fact]
    public void ComparisonStillPairsTwoRunsOfTheSameQueryWithDifferentParameterValues()
    {
        /* The reason this change had to be established before it was made. Substituting gives two
           executions of one query two different statement texts, so if pairing had keyed on that
           text, comparing a fast run against a slow one would have stopped matching them and
           reported two unrelated statements instead of one regression. It pairs on QueryHash, which
           is why substituting here is safe — and this fails the moment somebody changes that. */
        var slow = Analyze(ParameterizedPlan);
        var fast = ResultMapper.Map(
            ShowPlanParser.Parse(PlanXml(ParameterizedPlan).Replace("123456", "999999", StringComparison.Ordinal)),
            ParameterizedPlan);

        Assert.NotEqual(slow.Statements[0].StatementText, fast.Statements[0].StatementText);
        Assert.Equal(slow.Statements[0].QueryHash, fast.Statements[0].QueryHash);

        var comparison = ComparisonFormatter.Compare(slow, fast, "before", "after");

        Assert.Contains("--- Statement 1 ---", comparison);
        Assert.DoesNotContain("only in Plan", comparison);
    }

    [Fact]
    public async Task McpAnalyzePlan_HandsTheModelTheRunnableStatement()
    {
        /* The consumer that matters most: a model handed @0 with no values is being handed a
           question it cannot answer. */
        var manager = new PlanSessionManager();
        var sessionId = $"mcp-{Guid.NewGuid():N}";
        manager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = ParameterizedPlan,
            Source = "file",
            Plan = PlanTestHelper.LoadAndAnalyze(ParameterizedPlan)
        });
        var operations = new PlanOperations(manager, AnalyzerConfig.Default, enforceQueryAdmission: false);

        var json = await McpPlanTools.AnalyzePlan(
            manager,
            operations,
            sessionId,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var statement = document.RootElement.GetProperty("statements")[0];
        Assert.Contains(SubstitutedPredicate, statement.GetProperty("statement_text").GetString());
    }

    [Fact]
    public void McpReproScript_StillBuildsAParameterizedBody()
    {
        /* The one consumer that wants the other form. get_repro_script wraps this body in
           sp_executesql with a parameter list read out of the same plan; a body with the literal
           already inlined would declare a parameter it never uses, and would compile the
           constant-folded plan rather than the parameterized one the script exists to reproduce.

           A named parameter rather than the forced-parameterization plan, because ReproScriptBuilder
           requires a parameter name a human could have typed and drops @0 … @6 before it gets this
           far — so that plan never reaches the sp_executesql branch this is about. */
        var manager = new PlanSessionManager();
        var sessionId = $"repro-{Guid.NewGuid():N}";
        manager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = NamedParameterPlan,
            Source = "file",
            Plan = PlanTestHelper.LoadAndAnalyze(NamedParameterPlan)
        });

        var script = McpPlanTools.GetReproScript(manager, sessionId);

        Assert.Contains("sp_executesql", script);
        Assert.Contains("@date datetime", script);
        Assert.Contains("CreationDate >= @date", script);
        Assert.DoesNotContain("CreationDate >= '2013-01-01 00:00:00.000'", script);
    }

    [Fact]
    public void AssignmentTargets_AreLeftAloneRatherThanTurnedIntoComparisons()
    {
        /* SELECT @job_name = name, @owner_sid = owner_sid FROM msdb.dbo.sysjobs_view WHERE
           (job_id = @job_id) — both assigned variables carry a compiled value of NULL, and writing
           it over them gives "SELECT NULL = name", which reads as a comparison. The parameter that
           is actually read still gets its value. */
        var statement = Analyze("compile_memory_exceeded_plan.sqlplan").Statements[0];

        Assert.Contains("@job_name = name", statement.StatementText);
        Assert.Contains("@owner_sid = owner_sid", statement.StatementText);
        Assert.DoesNotContain("NULL = name", statement.StatementText);
        Assert.Contains("job_id = '846EFE14-2AEE-4A65-9EE0-213187F82250'", statement.StatementText);
    }

    [Fact]
    public void StatementWithNothingToSubstitute_KeepsItsTextAndCarriesNoSecondForm()
    {
        /* Nearly every plan. The mapped text has to stay byte-identical here, because the CLI's
           JSON output is hashed as a contract against a plan of exactly this shape — a change in
           these bytes is something to decide on, not to discover. */
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");

        var statement = ResultMapper.Map(plan, "row_goal_plan.sqlplan").Statements[0];

        Assert.Equal(PlanTestHelper.FirstStatement(plan).StatementText, statement.StatementText);
        Assert.Null(statement.ParameterizedStatementText);
    }

    private static AnalysisResult Analyze(string planFile) =>
        ResultMapper.Map(PlanTestHelper.LoadAndAnalyze(planFile), planFile);

    private static string PlanXml(string planFile) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", planFile));
}
