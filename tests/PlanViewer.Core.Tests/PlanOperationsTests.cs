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

        Assert.Matches("^row_goal_plan-[0-9a-f]{12}$", opened.SessionId);
        Assert.Equal("row_goal_plan.sqlplan", opened.Label);
        Assert.Equal(1, opened.StatementCount);
        Assert.Equal(2, opened.WarningCount);
        var session = catalog.GetSession(opened.SessionId);
        Assert.NotNull(session);
        Assert.Equal("row_goal_plan.sqlplan", session.Source);
        Assert.NotNull(session.Analysis);
        Assert.Empty(session.Plan.Batches);
    }

    [Fact]
    public async Task GetAnalysis_UsesTheSnapshotCapturedAtRegistration()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        var session = Assert.IsType<PlanViewer.Core.Models.PlanSession>(catalog.GetSession(opened.SessionId));

        Assert.Empty(session.Plan.Batches);
        var captured = operations.GetAnalysis(session);
        session.Plan.Batches.Clear();
        var afterMutation = operations.GetAnalysis(session);

        Assert.Same(captured, afterMutation);
        Assert.Equal(1, afterMutation.Summary.TotalStatements);
        Assert.Equal(2, afterMutation.Summary.TotalWarnings);
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
    public async Task GetWarnings_BoundsTheReturnedItemsAndReportsTruncation()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        var session = Assert.IsType<PlanViewer.Core.Models.PlanSession>(catalog.GetSession(opened.SessionId));
        var analysis = Assert.IsType<PlanViewer.Core.Output.AnalysisResult>(session.Analysis);
        var statement = Assert.Single(analysis.Statements);
        statement.Warnings = Enumerable.Range(0, PlanOperations.DefaultMaxWarningResults + 1)
            .Select(index => new PlanViewer.Core.Output.WarningResult
            {
                Type = $"warning-{index}",
                Severity = "Warning",
                Message = new string('x', PlanOperations.DefaultMaxResponseTextLength + 1)
            })
            .ToList();
        statement.OperatorTree = null;

        var result = operations.GetWarnings(session);

        Assert.Equal(PlanOperations.DefaultMaxWarningResults + 1, result.WarningCount);
        Assert.Equal(PlanOperations.DefaultMaxWarningResults, result.ReturnedWarningCount);
        Assert.Equal(PlanOperations.DefaultMaxWarningResults, result.Warnings.Count);
        Assert.True(result.Truncated);
        Assert.All(result.Warnings, warning =>
            Assert.True(warning.Message.Length <= PlanOperations.DefaultMaxResponseTextLength + 17));
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
    public async Task GetMissingIndexes_BoundsTheReturnedItemsAndReportsTruncation()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        var session = Assert.IsType<PlanViewer.Core.Models.PlanSession>(catalog.GetSession(opened.SessionId));
        var analysis = Assert.IsType<PlanViewer.Core.Output.AnalysisResult>(session.Analysis);
        var statement = Assert.Single(analysis.Statements);
        statement.MissingIndexes = Enumerable.Range(0, PlanOperations.DefaultMaxMissingIndexResults + 1)
            .Select(index => new PlanViewer.Core.Output.MissingIndexResult
            {
                Database = "database",
                SchemaName = "dbo",
                BareTable = $"table-{index}",
                Table = $"database.dbo.table-{index}",
                EqualityColumns = Enumerable.Repeat(new string('c', 600), 100).ToList(),
                CreateStatement = new string('x', PlanOperations.DefaultMaxResponseTextLength + 1)
            })
            .ToList();

        var result = operations.GetMissingIndexes(session);

        Assert.Equal(PlanOperations.DefaultMaxMissingIndexResults + 1, result.MissingIndexCount);
        Assert.Equal(PlanOperations.DefaultMaxMissingIndexResults, result.ReturnedIndexCount);
        Assert.Equal(PlanOperations.DefaultMaxMissingIndexResults, result.Indexes.Count);
        Assert.True(result.Truncated);
        Assert.All(result.Indexes, index =>
        {
            Assert.True(index.CreateStatement.Length <= PlanOperations.DefaultMaxResponseTextLength + 17);
            Assert.True(index.EqualityColumns.Count <= 64);
            Assert.All(index.EqualityColumns, column => Assert.True(column.Length <= 512 + 17));
        });
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

        Assert.Matches("^row_goal_plan-[0-9a-f]{12}$", first.SessionId);
        Assert.Matches("^row_goal_plan-[0-9a-f]{12}$", second.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task OpenAsync_BoundsTheHumanReadableSessionIdentity()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var source = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var directory = Directory.CreateTempSubdirectory("plan-long-id-");
        var path = Path.Combine(directory.FullName, $"{new string('a', 180)}.sqlplan");
        try
        {
            File.Copy(source, path);

            var opened = await operations.OpenAsync(path, TestContext.Current.CancellationToken);

            Assert.True(opened.SessionId.Length <= 61, opened.SessionId);
            Assert.Matches("^[a-z]{1,48}-[0-9a-f]{12}$", opened.SessionId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_UsesPlanFallbackWhenTheTruncatedLabelHasNoReadableCharacters()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var opened = await operations.OpenAsync(
            stream,
            $"{new string('.', 60)}.sqlplan",
            TestContext.Current.CancellationToken);

        Assert.Matches("^plan-[0-9a-f]{12}$", opened.SessionId);
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
    public async Task OpenAsync_AllocatesUniqueIdsAcrossConcurrentAdmissionWaves()
    {
        var catalog = new InMemoryPlanCatalog();
        var operations = new PlanOperations(catalog);
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);

        for (var wave = 0; wave < 4; wave++)
        {
            var opened = await Task.WhenAll(
                operations.OpenAsync(path, TestContext.Current.CancellationToken),
                operations.OpenAsync(path, TestContext.Current.CancellationToken));
            Assert.All(opened, result => Assert.True(sessionIds.Add(result.SessionId), result.SessionId));
        }

        Assert.Equal(8, sessionIds.Count);
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

            Assert.Matches($"^plan-{reservedName}-[0-9a-f]{{12}}$", opened.SessionId);
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
        for (var index = 0; index < PlanOperations.DefaultMaxSessions - 1; index++)
        {
            catalog.Register(new PlanViewer.Core.Models.PlanSession
            {
                SessionId = $"seed-{index}",
                Label = $"seed-{index}.sqlplan",
                Source = $"seed-{index}.sqlplan",
                Plan = new PlanViewer.Core.Models.ParsedPlan()
            });
        }

        var admitted = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
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

        Assert.Single(admitted, value => value);
        Assert.Equal(PlanOperations.DefaultMaxSessions, catalog.GetAllSessions().Count);
    }

    [Fact]
    public async Task OpenAsync_BoundsEstimatedRetainedAnalysisMemoryByComplexity()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog());
        var path = Path.Combine(Path.GetTempPath(), $"retained-complexity-{Guid.NewGuid():N}.sqlplan");
        try
        {
            var xml = new System.Text.StringBuilder(
                "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>");
            for (var id = 0; id < PlanOperations.DefaultMaxStatements; id++)
            {
                xml.Append($"<StmtSimple StatementId=\"{id}\" StatementText=\"SELECT 1\" StatementSubTreeCost=\"0\" />");
            }
            xml.Append("</Statements></Batch></BatchSequence></ShowPlanXML>");
            await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);

            var first = await operations.OpenAsync(path, TestContext.Current.CancellationToken);
            await operations.OpenAsync(path, TestContext.Current.CancellationToken);
            await operations.OpenAsync(path, TestContext.Current.CancellationToken);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => operations.OpenAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains("retained-analysis", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(operations.Close(first.SessionId));
            await operations.OpenAsync(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
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
