using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public sealed class PlanResourceBudgetTests
{
    [Fact]
    public async Task ForCatalog_SharesConcurrentOpenSlotsAcrossFacades()
    {
        var catalog = new InMemoryPlanCatalog();
        var first = PlanResourceBudget.ForCatalog(catalog);
        var second = PlanResourceBudget.ForCatalog(catalog);

        Assert.Same(first, second);
        using var firstLease = await first.AcquireOpenSlotAsync(TestContext.Current.CancellationToken);
        using var secondLease = await second.AcquireOpenSlotAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await first.AcquireOpenSlotAsync(TestContext.Current.CancellationToken));
        Assert.Contains("concurrent plan-open limit", error.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void TryRegister_ReportsCapacityInsteadOfMasqueradingItAsAnIdCollision()
    {
        var catalog = new InMemoryPlanCatalog();
        var budget = PlanResourceBudget.ForCatalog(catalog);
        Assert.True(catalog.TryRegister(CreateSession("first")));

        var error = Assert.Throws<InvalidOperationException>(
            () => budget.TryRegister(CreateSession("second"), maxSessions: 1));

        Assert.Contains("session limit of 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReserveRetainedEstimate_EnforcesAndReleasesTheAggregateBudget()
    {
        var budget = PlanResourceBudget.ForCatalog(new InMemoryPlanCatalog());
        using var first = budget.ReserveRetainedEstimate(PlanResourceBudget.DefaultMaxEstimatedRetainedBytes);

        Assert.Throws<InvalidOperationException>(() => budget.ReserveRetainedEstimate(1));

        first.Dispose();
        using var afterRelease = budget.ReserveRetainedEstimate(PlanResourceBudget.DefaultMaxEstimatedRetainedBytes);
    }

    [Fact]
    public async Task OpenOwnedStreamAsync_AcquiresTheSharedSlotBeforeInvokingTheFactory()
    {
        var operations = new PlanOperations(new InMemoryPlanCatalog(), PlanViewer.Core.Models.AnalyzerConfig.Default);
        var releaseFactories = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoFactoriesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeFactories = 0;
        var maxActiveFactories = 0;
        var path = Path.GetFullPath(Path.Combine("Plans", "row_goal_plan.sqlplan"));

        async ValueTask<(FileStream Stream, string Label, IAsyncDisposable Owner)> OpenAsync(
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeFactories);
            UpdateMaximum(ref maxActiveFactories, active);
            if (active == PlanOperations.DefaultMaxConcurrentOpens)
                twoFactoriesStarted.TrySetResult();

            await releaseFactories.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref activeFactories);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (stream, Path.GetFileName(path), stream);
        }

        var opens = Enumerable.Range(0, PlanOperations.DefaultMaxConcurrentOpens + 1)
            .Select(_ => operations.OpenOwnedStreamAsync(OpenAsync, TestContext.Current.CancellationToken))
            .ToArray();

        await twoFactoriesStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlanOperations.DefaultMaxConcurrentOpens, Volatile.Read(ref maxActiveFactories));
        Assert.Equal(PlanOperations.DefaultMaxConcurrentOpens, Volatile.Read(ref activeFactories));

        Assert.True(opens[^1].IsFaulted);
        releaseFactories.TrySetResult();
        await Task.WhenAll(opens[..PlanOperations.DefaultMaxConcurrentOpens])
            .WaitAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await opens[^1]);
        Assert.Contains("concurrent plan-open limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(PlanOperations.DefaultMaxConcurrentOpens, maxActiveFactories);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }


    private static PlanViewer.Core.Models.PlanSession CreateSession(string id) => new()
    {
        SessionId = id,
        Label = $"{id}.sqlplan",
        Source = $"{id}.sqlplan",
        Plan = new PlanViewer.Core.Models.ParsedPlan()
    };

}
