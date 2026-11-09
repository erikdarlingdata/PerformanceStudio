using System.Text;
using System.Text.Json;
using PlanViewer.App.Mcp;
using PlanViewer.Core.Interfaces;
using AnalyzerConfig = PlanViewer.Core.Models.AnalyzerConfig;
using CorePlanSession = PlanViewer.Core.Models.PlanSession;
using CorePlanSessionSummary = PlanViewer.Core.Models.PlanSessionSummary;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class McpPlanToolsContractTests
{
    [Fact]
    public void GetPlanWarnings_PreservesTheHistoricalStatementWarningScope()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);

        var result = McpPlanTools.GetPlanWarnings(manager, new PlanOperations(manager), session.SessionId);

        Assert.Equal("No warnings found in this plan.", result);
    }

    [Fact]
    public void GetPlanWarnings_PreservesHistoricalInvalidSeverityBehavior()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);

        var result = McpPlanTools.GetPlanWarnings(
            manager,
            new PlanOperations(manager),
            session.SessionId,
            "urgent");

        Assert.Equal("No urgent warnings found in this plan.", result);
    }

    [Fact]
    public void BoundedResponses_ExplicitlyReportTruncation()
    {
        var manager = new PlanSessionManager();
        var template = CreateEstimatedSession();
        var analysis = ResultMapper.Map(template.Plan, template.Source);
        var statement = Assert.Single(analysis.Statements);
        statement.Warnings = Enumerable.Range(0, PlanOperations.DefaultMaxWarningResults + 1)
            .Select(index => new WarningResult
            {
                Type = $"warning-{index}",
                Severity = "Warning",
                Message = "bounded"
            })
            .ToList();
        statement.MissingIndexes = Enumerable.Range(0, PlanOperations.DefaultMaxMissingIndexResults + 1)
            .Select(index => new MissingIndexResult
            {
                BareTable = $"table-{index}",
                Table = $"dbo.table-{index}",
                CreateStatement = "CREATE INDEX"
            })
            .ToList();
        var session = new CorePlanSession
        {
            SessionId = template.SessionId,
            Label = template.Label,
            Source = template.Source,
            Plan = template.Plan,
            Analysis = analysis,
            StatementCount = template.StatementCount,
            HasActualStats = template.HasActualStats,
            WarningCount = analysis.Summary.TotalWarnings,
            CriticalWarningCount = analysis.Summary.CriticalWarnings,
            MissingIndexCount = analysis.Summary.MissingIndexes
        };
        manager.Register(session);

        var operations = new PlanOperations(manager);
        using var warnings = JsonDocument.Parse(McpPlanTools.GetPlanWarnings(manager, operations, session.SessionId));
        using var indexes = JsonDocument.Parse(McpPlanTools.GetMissingIndexes(manager, operations, session.SessionId));

        Assert.True(warnings.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            PlanOperations.DefaultMaxWarningResults,
            warnings.RootElement.GetProperty("returned_warning_count").GetInt32());
        Assert.True(indexes.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            PlanOperations.DefaultMaxMissingIndexResults,
            indexes.RootElement.GetProperty("returned_index_count").GetInt32());
    }

    [Fact]
    public void GetExpensiveOperators_PreservesHistoricalBareObjectName()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        var node = PlanTestHelper.FirstStatement(session.Plan).RootNode!;
        while (node.ObjectName is null)
            node = Assert.Single(node.Children);
        node.ObjectName = "BareTable";
        node.FullObjectName = "[database].[dbo].[BareTable]";
        manager.Register(session);

        var json = McpPlanTools.GetExpensiveOperators(manager, new PlanOperations(manager), session.SessionId, 10);

        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("operators")
            .EnumerateArray()
            .Single(element => element.GetProperty("object_name").ValueKind != JsonValueKind.Null);
        Assert.Equal("BareTable", item.GetProperty("object_name").GetString());
    }

    [Fact]
    public void GetExpensiveOperators_UsesTheSessionSnapshotAfterLookup()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);
        var operations = new PlanOperations(new ThrowingCatalog());

        var json = McpPlanTools.GetExpensiveOperators(manager, operations, session.SessionId, 1);

        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("operators")[0];
        Assert.Equal(JsonValueKind.Number, item.GetProperty("actual_rows").ValueKind);
        Assert.Equal(0, item.GetProperty("actual_rows").GetInt64());
        Assert.Equal(0, item.GetProperty("actual_elapsed_ms").GetInt64());
    }

    /// <summary>
    /// #689: estimated_rows is the optimizer's estimate for one execution and actual_rows is the total
    /// across every execution, so on the inner side of a Nested Loops join the pair alone reads an exact
    /// estimate as a misestimate by the execution count. Node 4 of key_lookup_plan.sqlplan is that case:
    /// a Key Lookup estimated at 0.00964372 rows per execution that ran 117 times for 1 row. The item
    /// carries expected_rows, the figure the viewer's node label divides by (1 of 1.128), and
    /// actual_executions, the count it was multiplied by, and expected_rows is the same number
    /// RowEstimateHelper.GetExpectedRows gives the viewer for the node.
    /// </summary>
    [Fact]
    public void GetExpensiveOperators_InnerSideKeyLookup_CarriesExpectedRowsAndActualExecutions()
    {
        var manager = new PlanSessionManager();
        var session = CreateSession("key_lookup_plan.sqlplan");
        Assert.True(session.HasActualStats);
        manager.Register(session);

        var json = McpPlanTools.GetExpensiveOperators(manager, new PlanOperations(manager), session.SessionId, 10);

        using var document = JsonDocument.Parse(json);
        var lookup = document.RootElement.GetProperty("operators")
            .EnumerateArray()
            .Single(element => element.GetProperty("node_id").GetInt32() == 4);

        Assert.Equal(0.00964372, lookup.GetProperty("estimated_rows").GetDouble(), precision: 8);
        Assert.Equal(1L, lookup.GetProperty("actual_rows").GetInt64());

        Assert.True(lookup.TryGetProperty("actual_executions", out var executions), "the operator carries no actual_executions");
        Assert.Equal(117L, executions.GetInt64());

        Assert.True(lookup.TryGetProperty("expected_rows", out var expected), "the operator carries no expected_rows");
        Assert.Equal(1.12831524, expected.GetDouble(), precision: 8);

        var node = PlanTestHelper.FindNode(PlanTestHelper.FirstStatement(session.Plan).RootNode!, 4);
        Assert.NotNull(node);
        Assert.True(RowEstimateHelper.IsInnerSideOfNestedLoops(node));
        Assert.Equal(RowEstimateHelper.GetExpectedRows(node), expected.GetDouble(), precision: 8);
    }

    /// <summary>
    /// #689: the parser sets HasActualStats per operator from the operator's own RunTimeInformation,
    /// which an actual plan omits on some operators (a Compute Scalar here), so expected_rows and
    /// actual_executions are null for any operator without runtime statistics, not only on an
    /// estimated plan. In in_list_dynamic_seek_plan.sqlplan the Compute Scalars at nodes 3, 5 and 7
    /// carry no runtime statistics and give both as null beside actual_rows 0, while the inner-side
    /// Index Seek at node 9 (estimate 2 over 2 executions) gives expected_rows 4 and
    /// actual_executions 2 in the same response.
    /// </summary>
    [Fact]
    public void GetExpensiveOperators_ActualPlan_GivesNullsOnARuntimeLessOperatorBesideAPopulatedOne()
    {
        var manager = new PlanSessionManager();
        var session = CreateSession("in_list_dynamic_seek_plan.sqlplan");
        Assert.True(session.HasActualStats);
        manager.Register(session);

        var json = McpPlanTools.GetExpensiveOperators(manager, new PlanOperations(manager), session.SessionId, 100);

        using var document = JsonDocument.Parse(json);
        var operators = document.RootElement.GetProperty("operators").EnumerateArray().ToList();

        var seek = Assert.Single(operators, element => element.GetProperty("node_id").GetInt32() == 9);
        Assert.Equal("Index Seek", seek.GetProperty("physical_op").GetString());
        Assert.Equal(2L, seek.GetProperty("actual_rows").GetInt64());
        Assert.Equal(2L, seek.GetProperty("actual_executions").GetInt64());
        Assert.Equal(4.0, seek.GetProperty("expected_rows").GetDouble(), precision: 8);

        var runtimeLess = operators
            .Where(element => element.GetProperty("node_id").GetInt32() is 3 or 5 or 7)
            .ToList();
        Assert.Equal(3, runtimeLess.Count);
        Assert.All(runtimeLess, item =>
        {
            Assert.Equal("Compute Scalar", item.GetProperty("physical_op").GetString());
            Assert.Equal(0L, item.GetProperty("actual_rows").GetInt64());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("expected_rows").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("actual_executions").ValueKind);
        });
    }

    /// <summary>
    /// #689: an estimated plan has no execution counts, so expected_rows and actual_executions are
    /// null, not 0, on every operator, while estimated_rows stays the per-execution estimate.
    /// </summary>
    [Fact]
    public void GetExpensiveOperators_EstimatedPlan_CarriesNullExpectedRowsAndNullActualExecutions()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        Assert.False(session.HasActualStats);
        manager.Register(session);

        var json = McpPlanTools.GetExpensiveOperators(manager, new PlanOperations(manager), session.SessionId, 10);

        using var document = JsonDocument.Parse(json);
        var operators = document.RootElement.GetProperty("operators").EnumerateArray().ToList();
        Assert.NotEmpty(operators);
        Assert.All(operators, item =>
        {
            Assert.Equal(JsonValueKind.Number, item.GetProperty("estimated_rows").ValueKind);

            Assert.True(item.TryGetProperty("expected_rows", out var expected), "the operator carries no expected_rows");
            Assert.Equal(JsonValueKind.Null, expected.ValueKind);

            Assert.True(item.TryGetProperty("actual_executions", out var executions), "the operator carries no actual_executions");
            Assert.Equal(JsonValueKind.Null, executions.ValueKind);
        });
    }


    [Fact]
#pragma warning disable xUnit1051 // Intentionally verify an independently cancelled MCP request token.
    public async Task QueryStoreHandlers_PropagateRequestCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var manager = new PlanSessionManager();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => McpQueryStoreTools.CheckQueryStore(
                null!,
                null!,
                "server",
                "database",
                cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => McpQueryStoreTools.GetQueryStoreTop(
                manager,
                new PlanOperations(manager),
                null!,
                null!,
                "server",
                "database",
                cancellation.Token));
    }
#pragma warning restore xUnit1051

    [Fact]
    public async Task FullAnalysisHandlers_PropagateRequestCancellation()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);
        var operations = new PlanOperations(manager);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => McpPlanTools.AnalyzePlan(manager, operations, session.SessionId, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => McpPlanTools.GetPlanSummary(manager, operations, session.SessionId, cancellation.Token));
    }

    [Fact]
    public async Task AnalyzePlan_PreservesTheUiSessionSourceWhenMappedOnDemand()
    {
        var manager = new PlanSessionManager();
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");
        var sessionId = $"ui-{Guid.NewGuid():N}";
        manager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = "row_goal_plan.sqlplan",
            Source = "file",
            Plan = plan
        });
        var operations = new PlanOperations(manager, AnalyzerConfig.Default, enforceQueryAdmission: false);

        var json = await McpPlanTools.AnalyzePlan(
            manager,
            operations,
            sessionId,
            TestContext.Current.CancellationToken);
        var summary = await McpPlanTools.GetPlanSummary(
            manager,
            operations,
            sessionId,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("file", document.RootElement.GetProperty("plan_source").GetString());
        Assert.DoesNotContain("error", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetPlanWarnings(manager, operations, sessionId), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetMissingIndexes(manager, operations, sessionId), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetExpensiveOperators(manager, operations, sessionId), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GivenQueryStorePlan_WhenCaptured_ThenLegacyToolsRemainExactWithoutTheParserGraph()
    {
        const string sessionId = "query-store-session";
        const string label = "QS:database Q1 P2";
        const string queryText = "SELECT * FROM dbo.PostTypes WHERE Id = @PostTypeId";
        var legacyPlan = PlanTestHelper.LoadAndAnalyze("param-sniffing-posttypeid2.sqlplan");
        var capturedPlan = PlanTestHelper.LoadAndAnalyze("param-sniffing-posttypeid2.sqlplan");
        var legacyManager = new PlanSessionManager();
        legacyManager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = label,
            Source = "query-store",
            Plan = legacyPlan,
            QueryText = queryText
        });
        var capturedManager = new PlanSessionManager();
        var capturedOperations = new PlanOperations(capturedManager, AnalyzerConfig.Default);
        var captured = McpQueryStoreTools.CaptureSession(
            sessionId,
            label,
            capturedPlan,
            queryText,
            "server");
        capturedOperations.AdmitSnapshot(
            captured,
            Encoding.UTF8.GetByteCount(captured.RawPlanXml!),
            TestContext.Current.CancellationToken);
        var registered = Assert.IsType<CorePlanSession>(capturedManager.GetSession(sessionId));

        Assert.Equal("query-store", captured.Source);
        Assert.Equal(captured.RawPlanXml, registered.RawPlanXml);
        Assert.Equal(captured.DatabaseName, registered.DatabaseName);
        Assert.Equal("query-store", captured.Analysis?.PlanSource);
        Assert.Equal(label, captured.Label);
        Assert.False(string.IsNullOrEmpty(captured.RawPlanXml));
        Assert.Empty(captured.Plan.Batches);
        Assert.Empty(captured.Plan.RawXml);
        Assert.Equal(
            McpPlanTools.GetPlanParameters(legacyManager, sessionId),
            McpPlanTools.GetPlanParameters(capturedManager, sessionId));
        Assert.Equal(
            McpPlanTools.GetPlanXml(legacyManager, sessionId),
            McpPlanTools.GetPlanXml(capturedManager, sessionId));
        Assert.Equal(
            McpPlanTools.ComparePlans(legacyManager, sessionId, sessionId),
            McpPlanTools.ComparePlans(capturedManager, sessionId, sessionId));

        /* #697: the repro script stamps the second it was generated in, so the two scripts are
           generated in different seconds here, the way they are when the two calls happen to
           straddle a second boundary, and the comparison must hold all the same. */
        var legacyScript = McpPlanTools.GetReproScript(legacyManager, sessionId);
        var second = DateTime.Now.Second;
        Assert.True(
            SpinWait.SpinUntil(() => DateTime.Now.Second != second, TimeSpan.FromSeconds(3)),
            "the wall clock did not reach the next second");
        var capturedScript = McpPlanTools.GetReproScript(capturedManager, sessionId);
        Assert.Contains("Generated: ", legacyScript);
        Assert.Equal(legacyScript, capturedScript);
    }

    /// <summary>
    /// #681: analyze_plan lists, for each statement, the statistics the optimizer loaded while
    /// compiling it, with the status that tells a plan that recorded none apart from a statement with
    /// no plan. The fixture's statements are a DECLARE, a SELECT that loaded three statistics, and a
    /// SELECT whose plan has no OptimizerStatsUsage.
    /// </summary>
    [Fact]
    public async Task AnalyzePlan_ReportsTheStatisticsEachStatementLoaded()
    {
        var manager = new PlanSessionManager();
        var plan = PlanTestHelper.LoadAndAnalyze("stats_usage_plan.sqlplan");
        var sessionId = $"stats-{Guid.NewGuid():N}";
        manager.Register(new CorePlanSession
        {
            SessionId = sessionId,
            Label = "stats_usage_plan.sqlplan",
            Source = "file",
            Plan = plan
        });
        var operations = new PlanOperations(manager, AnalyzerConfig.Default, enforceQueryAdmission: false);

        var json = await McpPlanTools.AnalyzePlan(
            manager,
            operations,
            sessionId,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var statements = document.RootElement.GetProperty("statements").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "no_query_plan", "recorded", "not_in_plan" },
            statements.Select(s => s.GetProperty("statistics_status").GetString()));

        var rows = statements[1].GetProperty("statistics_loaded").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(10, statements[1].GetProperty("statistics_cap").GetInt32());
        Assert.Equal(900000, rows[0].GetProperty("modification_count").GetInt64());
        Assert.Equal("Customers", rows[0].GetProperty("table").GetString());
        Assert.Equal("Orders", rows[1].GetProperty("table").GetString());
        Assert.Equal("#orders", rows[2].GetProperty("table").GetString());
        Assert.Equal(JsonValueKind.Null, statements[1].GetProperty("statistics_note").ValueKind);
        Assert.Equal(JsonValueKind.String, statements[2].GetProperty("statistics_note").ValueKind);
    }

    private static CorePlanSession CreateEstimatedSession() => CreateSession("row_goal_plan.sqlplan");

    private static CorePlanSession CreateSession(string planFileName)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(planFileName);
        var analysis = ResultMapper.Map(plan, planFileName);
        return new CorePlanSession
        {
            SessionId = $"contract-{Guid.NewGuid():N}",
            Label = planFileName,
            Source = planFileName,
            Plan = plan,
            StatementCount = analysis.Summary.TotalStatements,
            HasActualStats = analysis.Summary.HasActualStats,
            WarningCount = analysis.Summary.TotalWarnings,
            CriticalWarningCount = analysis.Summary.CriticalWarnings,
            MissingIndexCount = analysis.Summary.MissingIndexes
        };
    }

    private sealed class ThrowingCatalog : IPlanCatalog
    {
        public void Register(CorePlanSession session) => throw new InvalidOperationException();
        public bool TryRegister(CorePlanSession session) => throw new InvalidOperationException();
        public bool Unregister(string sessionId) => throw new InvalidOperationException();
        public CorePlanSession? GetSession(string sessionId) => throw new InvalidOperationException("Catalog was queried twice.");
        public IReadOnlyList<CorePlanSessionSummary> GetAllSessions() => throw new InvalidOperationException();
    }
}
