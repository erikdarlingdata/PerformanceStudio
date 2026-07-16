using System.Text.Json;
using PlanViewer.App.Mcp;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
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

    private static PlanSession CreateEstimatedSession()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");
        var analysis = ResultMapper.Map(plan, "row_goal_plan.sqlplan");
        return new PlanSession
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
        public void Register(PlanSession session) => throw new InvalidOperationException();
        public bool TryRegister(PlanSession session) => throw new InvalidOperationException();
        public bool Unregister(string sessionId) => throw new InvalidOperationException();
        public PlanSession? GetSession(string sessionId) => throw new InvalidOperationException("Catalog was queried twice.");
        public IReadOnlyList<PlanSessionSummary> GetAllSessions() => throw new InvalidOperationException();
    }
}
