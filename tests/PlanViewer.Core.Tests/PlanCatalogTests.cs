using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PlanCatalogTests
{
    [Fact]
    public void Register_ListLookupAndRemove_UseTheSharedSessionContract()
    {
        var catalog = new InMemoryPlanCatalog();
        var beta = CreateSession("beta", "Beta");
        var alpha = CreateSession("alpha", "Alpha");

        catalog.Register(beta);
        catalog.Register(alpha);

        Assert.Same(beta, catalog.GetSession("beta"));
        Assert.Collection(
            catalog.GetAllSessions(),
            item => Assert.Equal("alpha", item.SessionId),
            item => Assert.Equal("beta", item.SessionId));
        Assert.True(catalog.Unregister("beta"));
        Assert.Null(catalog.GetSession("beta"));
        Assert.False(catalog.Unregister("missing"));
    }

    private static PlanSession CreateSession(string id, string label) => new()
    {
        SessionId = id,
        Label = label,
        Source = "file",
        Plan = new ParsedPlan(),
        StatementCount = 1,
        WarningCount = 2,
        CriticalWarningCount = 1,
        MissingIndexCount = 3,
        HasActualStats = true
    };
}
