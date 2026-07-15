using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PlanOperationsTests
{
    [Fact]
    public async Task OpenAsync_LoadsAnalyzesAndRegistersAPlanFile()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("row_goal_plan", opened.SessionId);
        Assert.Equal("row_goal_plan.sqlplan", opened.Label);
        Assert.Equal(1, opened.StatementCount);
        Assert.Equal(2, opened.WarningCount);
        var session = catalog.GetSession(opened.SessionId);
        Assert.NotNull(session);
        Assert.Equal("row_goal_plan.sqlplan", session.Source);
        Assert.NotEmpty(session.Plan.Batches);
    }

    [Fact]
    public async Task GetSummary_ReturnsAConciseTypedProjection()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        var summary = operations.GetSummary(opened.SessionId);

        Assert.Equal(opened.SessionId, summary.SessionId);
        Assert.Equal(1, summary.TotalStatements);
        Assert.Equal(2, summary.TotalWarnings);
        Assert.Equal(0, summary.CriticalWarnings);
        Assert.False(summary.HasActualStats);
        Assert.Contains("Row Goal", summary.WarningTypes);
    }

    [Fact]
    public async Task GetWarnings_FiltersAllStatementAndOperatorWarningsBySeverity()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        var warnings = operations.GetWarnings(opened.SessionId, "warning");

        var warning = Assert.Single(warnings.Warnings);
        Assert.Equal(1, warnings.WarningCount);
        Assert.Equal("Warning", warning.Severity);
        Assert.Equal("Top Above Scan", warning.Type);
        Assert.NotNull(warning.NodeId);
        Assert.NotEmpty(warning.Statement);
    }


    [Fact]
    public async Task GetExpensiveOperators_RanksEstimatedPlansByCostAndHonorsTop()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        var result = operations.GetExpensiveOperators(opened.SessionId, 1);

        Assert.Equal("cost_percent", result.RankedBy);
        var item = Assert.Single(result.Operators);
        Assert.Equal("Index Scan", item.PhysicalOp);
        Assert.Equal(80, item.CostPercent);
    }


    [Fact]
    public async Task GetMissingIndexes_ReturnsStructuredSuggestions()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "top_above_scan_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        var result = operations.GetMissingIndexes(opened.SessionId);

        Assert.Equal(opened.SessionId, result.SessionId);
        Assert.Equal(result.Indexes.Count, result.MissingIndexCount);
        var index = Assert.Single(result.Indexes);
        Assert.NotEmpty(index.Table);
        Assert.Contains("CREATE", index.CreateStatement, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task OpenAsync_UsesUniqueSessionIdsForTheSameFile()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        var first = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        var second = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("row_goal_plan", first.SessionId);
        Assert.Equal("row_goal_plan-2", second.SessionId);
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingFiles()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var missing = Path.GetFullPath(Path.Combine("Plans", "missing.sqlplan"));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => operations.OpenAsync(missing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FiltersAndTop_RejectInvalidValues()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentException>(() => operations.GetWarnings(opened.SessionId, "urgent"));
        Assert.Throws<ArgumentOutOfRangeException>(() => operations.GetExpensiveOperators(opened.SessionId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => operations.GetExpensiveOperators(opened.SessionId, 101));
    }


    [Fact]
    public async Task OpenAsync_AllocatesUniqueIdsConcurrently()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        var opened = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => operations.OpenAsync(path, TestContext.Current.CancellationToken)));

        Assert.Equal(8, opened.Select(result => result.SessionId).Distinct().Count());
        Assert.Equal(8, catalog.GetAllSessions().Count);
    }

}
