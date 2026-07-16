using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PlanAnalysisPipelineTests
{
    [Fact]
    public void Analyze_RunsTheSharedParseAnalyzeAndScorePipeline()
    {
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));
        var xml = File.ReadAllText(path);

        var plan = PlanAnalysisPipeline.Analyze(
            xml,
            AnalyzerConfig.Default,
            TestContext.Current.CancellationToken);
        var result = ResultMapper.Map(plan, Path.GetFileName(path));

        Assert.Null(plan.ParseError);
        Assert.Equal(2, result.Summary.TotalWarnings);
        Assert.Contains("Row Goal", result.Summary.WarningTypes);
    }

    [Fact]
    public void Analyze_ObservesCancellationDuringStatementAnalysis()
    {
        var plan = new ParsedPlan
        {
            Batches =
            [
                new PlanBatch
                {
                    Statements = Enumerable.Range(0, 20_000)
                        .Select(index => new PlanStatement
                        {
                            StatementId = index,
                            StatementText = "SELECT * FROM dbo.example WHERE name LIKE '%value'"
                        })
                        .ToList()
                }
            ]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PlanAnalyzer.AnalyzeCancellable(plan, AnalyzerConfig.Default, serverMetadata: null, cancellation.Token));
    }

    [Fact]
    public void ResultMapper_DetachesCollectionsFromTheParserGraph()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("top_above_scan_plan.sqlplan");
        var statement = Assert.Single(plan.Batches.SelectMany(batch => batch.Statements));
        var sourceIndex = Assert.Single(statement.MissingIndexes);

        var analysis = ResultMapper.Map(plan, "top_above_scan_plan.sqlplan");
        var mappedStatement = Assert.Single(analysis.Statements);
        var mappedIndex = Assert.Single(mappedStatement.MissingIndexes);

        Assert.NotSame(sourceIndex.EqualityColumns, mappedIndex.EqualityColumns);
        Assert.NotSame(sourceIndex.InequalityColumns, mappedIndex.InequalityColumns);
        Assert.NotSame(sourceIndex.IncludeColumns, mappedIndex.IncludeColumns);
    }

}
