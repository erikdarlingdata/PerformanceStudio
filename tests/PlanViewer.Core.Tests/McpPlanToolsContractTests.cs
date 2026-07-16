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

        using var warnings = JsonDocument.Parse(McpPlanTools.GetPlanWarnings(manager, session.SessionId));
        using var indexes = JsonDocument.Parse(McpPlanTools.GetMissingIndexes(manager, session.SessionId));

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


    [Fact]
    public void FullAnalysisHandlers_PropagateRequestCancellation()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);
        var operations = new PlanOperations(manager);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => McpPlanTools.AnalyzePlan(manager, operations, session.SessionId, cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => McpPlanTools.GetPlanSummary(manager, operations, session.SessionId, cancellation.Token));
    }

    [Fact]
    public void AnalyzePlan_PreservesTheUiSessionSourceWhenASnapshotIsPresent()
    {
        var manager = new PlanSessionManager();
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");
        var sessionId = $"ui-{Guid.NewGuid():N}";
        manager.Register(sessionId, new PlanViewer.App.Mcp.PlanSession
        {
            SessionId = sessionId,
            Label = "row_goal_plan.sqlplan",
            Source = "file",
            Plan = plan,
            Analysis = ResultMapper.Map(plan, "file")
        });

        var json = McpPlanTools.AnalyzePlan(manager, sessionId);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("file", document.RootElement.GetProperty("plan_source").GetString());
        Assert.DoesNotContain("error", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetPlanSummary(manager, sessionId), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetPlanWarnings(manager, sessionId), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetMissingIndexes(manager, sessionId), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", McpPlanTools.GetExpensiveOperators(manager, sessionId), StringComparison.OrdinalIgnoreCase);
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
        legacyManager.Register(sessionId, new PlanViewer.App.Mcp.PlanSession
        {
            SessionId = sessionId,
            Label = label,
            Source = "query-store",
            Plan = legacyPlan,
            QueryText = queryText
        });
        var capturedManager = new PlanSessionManager();
        var captured = McpQueryStoreTools.CaptureSession(
            sessionId,
            label,
            capturedPlan,
            queryText,
            "server");
        capturedManager.Register(sessionId, captured);

        Assert.Equal("query-store", captured.Source);
        Assert.Equal("query-store", captured.Analysis?.PlanSource);
        Assert.Equal(label, captured.Label);
        Assert.False(string.IsNullOrEmpty(captured.CapturedRawXml));
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
        Assert.Equal(
            McpPlanTools.GetReproScript(legacyManager, sessionId),
            McpPlanTools.GetReproScript(capturedManager, sessionId));
    }

    [Fact]
    public void HistoricalOverloads_DelegateToTheSharedOperationsBehavior()
    {
        var manager = new PlanSessionManager();
        var session = CreateEstimatedSession();
        manager.Register(session);
        var operations = new PlanOperations(manager, AnalyzerConfig.Default);

        Assert.Equal(
            McpPlanTools.AnalyzePlan(
                manager,
                operations,
                session.SessionId,
                TestContext.Current.CancellationToken),
            McpPlanTools.AnalyzePlan(manager, session.SessionId));
        Assert.Equal(
            McpPlanTools.GetPlanSummary(
                manager,
                operations,
                session.SessionId,
                TestContext.Current.CancellationToken),
            McpPlanTools.GetPlanSummary(manager, session.SessionId));
        Assert.Equal(
            McpPlanTools.GetPlanWarnings(manager, operations, session.SessionId, "Warning"),
            McpPlanTools.GetPlanWarnings(manager, session.SessionId, "Warning"));
        Assert.Equal(
            McpPlanTools.GetMissingIndexes(manager, operations, session.SessionId),
            McpPlanTools.GetMissingIndexes(manager, session.SessionId));
        Assert.Equal(
            McpPlanTools.GetExpensiveOperators(manager, operations, session.SessionId, 3),
            McpPlanTools.GetExpensiveOperators(manager, session.SessionId, 3));
    }

    private static CorePlanSession CreateEstimatedSession()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");
        var analysis = ResultMapper.Map(plan, "row_goal_plan.sqlplan");
        return new CorePlanSession
        {
            SessionId = $"contract-{Guid.NewGuid():N}",
            Label = "row_goal_plan.sqlplan",
            Source = "row_goal_plan.sqlplan",
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
