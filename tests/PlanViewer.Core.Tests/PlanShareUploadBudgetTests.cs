using PlanShare;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The share server lets one client key store a fixed number of bytes per UTC day. The clock is
/// injected so the day change can be tested without waiting for it.
/// </summary>
public class PlanShareUploadBudgetTests
{
    private const long Limit = 100;

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Charges_UpToTheLimit_AreAllowed()
    {
        var budget = new UploadBudget(Limit, new FakeClock());

        Assert.True(budget.TryCharge("client", 60));
        Assert.True(budget.TryCharge("client", 40));
    }

    [Fact]
    public void ChargePastTheLimit_IsRefused()
    {
        var budget = new UploadBudget(Limit, new FakeClock());
        Assert.True(budget.TryCharge("client", 100));

        Assert.False(budget.TryCharge("client", 1));
    }

    [Fact]
    public void SingleChargeLargerThanTheLimit_IsRefused()
    {
        var budget = new UploadBudget(Limit, new FakeClock());

        Assert.False(budget.TryCharge("client", Limit + 1));
    }

    [Fact]
    public void RefusedCharge_AddsNothingToTheTotal()
    {
        var budget = new UploadBudget(Limit, new FakeClock());
        Assert.True(budget.TryCharge("client", 90));

        Assert.False(budget.TryCharge("client", 20));
        Assert.True(budget.TryCharge("client", 10));
    }

    [Fact]
    public void EachClientKey_HasItsOwnBudget()
    {
        var budget = new UploadBudget(Limit, new FakeClock());
        Assert.True(budget.TryCharge("first", 100));

        Assert.False(budget.TryCharge("first", 1));
        Assert.True(budget.TryCharge("second", 100));
    }

    [Fact]
    public void Budget_ResetsAtTheStartOfTheNextUtcDay()
    {
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 9, 28, 23, 59, 59, TimeSpan.Zero) };
        var budget = new UploadBudget(Limit, clock);
        Assert.True(budget.TryCharge("client", 100));
        Assert.False(budget.TryCharge("client", 1));

        clock.Now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.True(budget.TryCharge("client", 100));
        Assert.False(budget.TryCharge("client", 1));
    }

    [Fact]
    public void Day_IsTheUtcDay_NotTheLocalDay()
    {
        // Both times are on 28 September at UTC-5, but 18:30 is 23:30 UTC and 19:30 is 00:30 UTC
        // on the next day, so the second charge lands in a new budget.
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 9, 28, 18, 30, 0, TimeSpan.FromHours(-5)) };
        var budget = new UploadBudget(Limit, clock);
        Assert.True(budget.TryCharge("client", 100));
        Assert.False(budget.TryCharge("client", 1));

        clock.Now = new DateTimeOffset(2026, 9, 28, 19, 30, 0, TimeSpan.FromHours(-5));

        Assert.True(budget.TryCharge("client", 100));
    }

    [Fact]
    public void Sweep_RemovesKeysFromEarlierDays_AndKeepsTodaysCharges()
    {
        var clock = new FakeClock();
        var budget = new UploadBudget(Limit, clock);
        Assert.True(budget.TryCharge("old-1", 10));
        Assert.True(budget.TryCharge("old-2", 10));

        clock.Now = clock.Now.AddDays(1);
        Assert.True(budget.TryCharge("current", 60));

        Assert.Equal(2, budget.Sweep());
        Assert.Equal(0, budget.Sweep());
        Assert.False(budget.TryCharge("current", 50));
        Assert.True(budget.TryCharge("current", 40));
    }

    [Fact]
    public void ConcurrentCharges_NeverPassTheLimit()
    {
        const int limit = 1000;
        var budget = new UploadBudget(limit, new FakeClock());
        var allowed = 0;

        Parallel.For(0, 4000, _ =>
        {
            if (budget.TryCharge("client", 1))
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(limit, allowed);
    }
}
