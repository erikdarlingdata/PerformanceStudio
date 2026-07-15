using PlanViewer.Cli.ReplSurface;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using Repl.Testing;

namespace PlanViewer.Core.Tests;

public sealed class PlanReplTests
{
    [Fact]
    public async Task InteractiveSession_OpenNavigatesAndReusesTypedHandlers()
    {
        var catalog = new InMemoryPlanCatalog();
        await using var host = ReplTestHost.Create(() => PlanReplApp.Create(catalog));
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = await host.OpenSessionAsync(cancellationToken: cancellationToken);
        var path = Path.GetFullPath(Path.Combine("Plans", "top_above_scan_plan.sqlplan"));

        var command = string.Concat("plan open ", (char)34, path, (char)34, " --no-logo");
        var opened = await session.RunCommandAsync(command, cancellationToken);
        var id = Assert.Single(catalog.GetAllSessions()).SessionId;
        var summary = await session.RunCommandAsync($"plan {id} summary --json --no-logo", cancellationToken);
        var warnings = await session.RunCommandAsync($"plan {id} warnings --json --no-logo", cancellationToken);
        var operators = await session.RunCommandAsync($"plan {id} operators --top 1 --json --no-logo", cancellationToken);
        var indexes = await session.RunCommandAsync($"plan {id} missing-indexes --json --no-logo", cancellationToken);

        Assert.Equal(0, opened.ExitCode);
        Assert.True(summary.ExitCode == 0, $"Summary failed: {summary.OutputText}");
        Assert.True(warnings.ExitCode == 0, $"Warnings failed: {warnings.OutputText}");
        Assert.True(operators.ExitCode == 0, $"Operators failed: {operators.OutputText}");
        Assert.True(indexes.ExitCode == 0, $"Indexes failed: {indexes.OutputText}");
        Assert.Equal(1, summary.GetResult<PlanSummaryResult>().TotalStatements);
        Assert.NotEmpty(warnings.GetResult<PlanWarningsResult>().Warnings);
        Assert.Single(operators.GetResult<ExpensiveOperatorsResult>().Operators);
        Assert.Single(indexes.GetResult<MissingIndexesResult>().Indexes);
    }

    [Fact]
    public async Task OneShotList_UsesJsonStructuredOutput()
    {
        await using var host = ReplTestHost.Create(PlanReplApp.Create);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = await host.OpenSessionAsync(cancellationToken: cancellationToken);

        var execution = await session.RunCommandAsync("plan list --json --no-logo", cancellationToken);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("[]", execution.OutputText);
    }
}
