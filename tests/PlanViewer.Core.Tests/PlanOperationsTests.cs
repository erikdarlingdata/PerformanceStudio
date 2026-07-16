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

        Assert.Matches("^row_goal_plan-[0-9a-f]{32}$", opened.SessionId);
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

        Assert.Matches("^row_goal_plan-[0-9a-f]{32}$", first.SessionId);
        Assert.Matches("^row_goal_plan-[0-9a-f]{32}$", second.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
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

    [Theory]
    [InlineData("open")]
    [InlineData("list")]
    public async Task OpenAsync_ReservesLiteralCommandNamesInSessionIds(string reservedName)
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var source = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var directory = Directory.CreateTempSubdirectory("plan-id-");
        var path = Path.Combine(directory.FullName, $"{reservedName}.sqlplan");
        try
        {
            File.Copy(source, path);

            var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

            Assert.Matches($"^plan-{reservedName}-[0-9a-f]{{32}}$", opened.SessionId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_SurfacesTheParserErrorForMalformedXml()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.sqlplan");
        try
        {
            await File.WriteAllTextAsync(path, "<ShowPlanXML>", TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("XML", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Could not parse any statements", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_HonorsCancellationWithoutRegisteringASession()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operations.OpenAsync(path, cancellation.Token));

        Assert.Empty(catalog.GetAllSessions());
    }

    [Fact]
    public async Task OpenAsync_RejectsPlansOverTheStatementComplexityBudget()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.Combine(Path.GetTempPath(), $"complex-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new System.Text.StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>");
            for (var id = 0; id <= PlanOperations.DefaultMaxStatements; id++)
            {
                xml.Append($"<StmtSimple StatementId=\"{id}\" StatementText=\"SELECT 1\" StatementSubTreeCost=\"0\" />");
            }
            xml.Append("</Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_DoesNotReuseAClosedSessionIdentity()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var first = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        Assert.True(operations.Close(first.SessionId));

        var second = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task OpenAsync_CountsNestedUdfStatementsAgainstTheComplexityBudget()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.Combine(Path.GetTempPath(), $"nested-complex-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new System.Text.StringBuilder(
                """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementId="1" StatementText="SELECT dbo.f()"><QueryPlan /><UDF ProcName="f"><Statements>""");
            for (var id = 0; id < PlanOperations.DefaultMaxStatements; id++)
            {
                xml.Append($"""<StmtSimple StatementId="{id + 2}" StatementText="SELECT 1" StatementSubTreeCost="0" />""");
            }
            xml.Append("</Statements></UDF></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("statement complexity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsFilesLargerThanTheDefaultBudget()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.Combine(Path.GetTempPath(), $"oversized-{Guid.NewGuid():N}.sqlplan");
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(16 * 1024 * 1024 + 1);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsMoreThanTheDefaultSessionBudget()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        for (var index = 0; index < 32; index++)
            await operations.OpenAsync(path, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("session limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_EnforcesTheSessionBudgetAtomicallyUnderConcurrency()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        var admitted = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            try
            {
                await operations.OpenAsync(path, TestContext.Current.CancellationToken);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Equal(32, admitted.Count(value => value));
        Assert.Equal(32, catalog.GetAllSessions().Count);
    }

    [Fact]
    public async Task OpenAsync_RejectsFilesWithoutSqlPlanExtension()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var source = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var path = Path.Combine(Path.GetTempPath(), $"plan-{Guid.NewGuid():N}.xml");
        try
        {
            File.Copy(source, path);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains(".sqlplan", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

}
