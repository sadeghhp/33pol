using Pol33.Core.Observability;
using Pol33.Observability.Metrics;

namespace Pol33.Observability.Tests.Metrics;

public sealed class MetricCallerBudgetTests
{
    [Fact]
    public void Resolve_WithinTheBudget_KeepsEveryCallerItsOwnSeries()
    {
        var budget = new MetricCallerBudget(maxCallers: 10);

        var first = budget.Resolve(new MetricCaller("fanus", "campaign"));
        var second = budget.Resolve(new MetricCaller("mofid", "app"));

        first.Should().Be(new MetricCaller("fanus", "campaign"));
        second.Should().Be(new MetricCaller("mofid", "app"));
        budget.TrackedCallers.Should().Be(2);
    }

    [Fact]
    public void Resolve_TheSameCallerAgain_SpendsNothing()
    {
        var budget = new MetricCallerBudget(maxCallers: 10);

        for (var i = 0; i < 50; i++)
        {
            budget.Resolve(new MetricCaller("fanus", "campaign"));
        }

        budget.TrackedCallers.Should().Be(1);
    }

    /// <summary>
    /// The point of folding by tenant: a tenant-level sum must keep adding up after the budget is
    /// spent, or a team with many keys would vanish from its own alert.
    /// </summary>
    [Fact]
    public void Resolve_PastTheBudget_FoldsANewKeyIntoOther_UnderItsOwnTenant()
    {
        var budget = Filled(out _);

        budget.Resolve(new MetricCaller("tenant-0", "a-key-nobody-has-seen"))
            .Should().Be(new MetricCaller("tenant-0", "other"));
    }

    [Fact]
    public void Resolve_PastTheBudget_FoldsAnUnseenTenantIntoOtherEntirely()
    {
        var budget = Filled(out _);

        budget.Resolve(new MetricCaller("late-tenant", "key")).Should().Be(MetricCaller.Other);
    }

    /// <summary>
    /// Nobody is evicted to make room: an exported series lives as long as the process does, so
    /// rotating callers through the budget would grow the exposition without bound.
    /// </summary>
    [Fact]
    public void Resolve_PastTheBudget_KeepsTheCallersAlreadyTracked()
    {
        var budget = Filled(out var tracked);

        budget.Resolve(new MetricCaller("late-tenant", "key"));

        budget.Resolve(tracked[0]).Should().Be(tracked[0]);
        budget.TrackedCallers.Should().Be(budget.MaxCallers);
    }

    [Fact]
    public void Resolve_AnUnspecifiedCaller_IsAnonymous_AndSpendsNothing()
    {
        var budget = new MetricCallerBudget(maxCallers: 10);

        budget.Resolve(default).Should().Be(MetricCaller.Anonymous);
        budget.TrackedCallers.Should().Be(0);
    }

    [Fact]
    public void Resolve_UnderContention_NeverTracksMoreThanTheBudget()
    {
        var budget = new MetricCallerBudget(maxCallers: 10);

        Parallel.For(0, 2_000, i => budget.Resolve(new MetricCaller("t", "key-" + (i % 200))));

        budget.TrackedCallers.Should().Be(10);
    }

    private static MetricCallerBudget Filled(out MetricCaller[] tracked)
    {
        var budget = new MetricCallerBudget(maxCallers: 10);
        tracked = Enumerable.Range(0, budget.MaxCallers)
            .Select(i => new MetricCaller("tenant-" + i, "key-" + i))
            .ToArray();

        foreach (var caller in tracked)
        {
            budget.Resolve(caller);
        }

        return budget;
    }
}
