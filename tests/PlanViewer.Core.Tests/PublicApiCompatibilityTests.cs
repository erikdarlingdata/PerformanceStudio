using PlanViewer.App.Mcp;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using CorePlanSession = PlanViewer.Core.Models.PlanSession;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PublicApiCompatibilityTests
{
    [Fact]
    public void PlanCatalog_PreservesTheOriginalTryRegisterSignature()
    {
        Assert.NotNull(typeof(IPlanCatalog).GetMethod(
            nameof(IPlanCatalog.TryRegister),
            [typeof(CorePlanSession)]));
        Assert.NotNull(typeof(PlanSessionManager).GetMethod(
            nameof(PlanSessionManager.TryRegister),
            [typeof(CorePlanSession)]));
    }

    [Fact]
    public void PlanOperations_PreservesTheOriginalWarningAndOperatorSignatures()
    {
        Assert.NotNull(typeof(PlanOperations).GetMethod(
            nameof(PlanOperations.GetWarnings),
            [typeof(string), typeof(string)]));
        Assert.NotNull(typeof(PlanOperations).GetMethod(
            nameof(PlanOperations.GetExpensiveOperators),
            [typeof(string), typeof(int)]));
    }
    [Fact]
    public void AppMcp_PreservesHistoricalSessionTypesAndManagerSignatures()
    {
        var appAssembly = typeof(PlanSessionManager).Assembly;
        var sessionType = appAssembly.GetType("PlanViewer.App.Mcp.PlanSession");
        var summaryType = appAssembly.GetType("PlanViewer.App.Mcp.PlanSessionSummary");
        Assert.NotNull(sessionType);
        Assert.NotNull(summaryType);

        var unregister = typeof(PlanSessionManager).GetMethod("Unregister", [typeof(string)]);
        var getSession = typeof(PlanSessionManager).GetMethod("GetSession", [typeof(string)]);
        var getAllSessions = typeof(PlanSessionManager).GetMethod("GetAllSessions", Type.EmptyTypes);
        Assert.NotNull(unregister);
        Assert.NotNull(getSession);
        Assert.NotNull(getAllSessions);
        Assert.Equal(typeof(void), unregister.ReturnType);
        Assert.Equal(sessionType, getSession.ReturnType);
        Assert.Equal(typeof(IReadOnlyList<>).MakeGenericType(summaryType), getAllSessions.ReturnType);
        Assert.NotNull(typeof(PlanSessionManager).GetMethod("Register", [typeof(string), sessionType]));
    }

    [Fact]
    public void AppMcp_PreservesHistoricalPlanToolOverloads()
    {
        var manager = typeof(PlanSessionManager);
        Assert.NotNull(typeof(McpPlanTools).GetMethod("AnalyzePlan", [manager, typeof(string)]));
        Assert.NotNull(typeof(McpPlanTools).GetMethod("GetPlanSummary", [manager, typeof(string)]));
        Assert.NotNull(typeof(McpPlanTools).GetMethod("GetPlanWarnings", [manager, typeof(string), typeof(string)]));
        Assert.NotNull(typeof(McpPlanTools).GetMethod("GetMissingIndexes", [manager, typeof(string)]));
        Assert.NotNull(typeof(McpPlanTools).GetMethod("GetExpensiveOperators", [manager, typeof(string), typeof(int)]));
    }


    [Fact]
    public void AppMcp_ManagerPreservesHistoricalRegistrationKeySemantics()
    {
        var manager = new PlanSessionManager();
        var session = new PlanViewer.App.Mcp.PlanSession
        {
            SessionId = "payload-id",
            Label = "compat.sqlplan",
            Source = "compat.sqlplan",
            Plan = new ParsedPlan()
        };

        manager.Register("catalog-key", session);

        Assert.Same(session, manager.GetSession("catalog-key"));
        Assert.Null(manager.GetSession("payload-id"));
        Assert.Equal("payload-id", Assert.Single(manager.GetAllSessions()).SessionId);
    }

}
