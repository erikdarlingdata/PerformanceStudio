using PlanViewer.App.Mcp;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PublicApiCompatibilityTests
{
    [Fact]
    public void PlanCatalog_PreservesTheOriginalTryRegisterSignature()
    {
        Assert.NotNull(typeof(IPlanCatalog).GetMethod(
            nameof(IPlanCatalog.TryRegister),
            [typeof(PlanSession)]));
        Assert.NotNull(typeof(PlanSessionManager).GetMethod(
            nameof(PlanSessionManager.TryRegister),
            [typeof(PlanSession)]));
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
    public void InMemoryPlanCatalog_RemainsUnsealed() =>
        Assert.False(typeof(InMemoryPlanCatalog).IsSealed);
}
