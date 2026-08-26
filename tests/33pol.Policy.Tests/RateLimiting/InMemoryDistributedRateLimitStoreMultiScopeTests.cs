using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Multi-scope admission: all-or-nothing across the rule set, and the refund that makes it so.
/// </summary>
public sealed class InMemoryDistributedRateLimitStoreMultiScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquireAll_WhenEveryScopeHasRoom_Admits()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var rules = Rules(("t:acme", 10), ("m:gpt-4", 10));

        store.TryAcquireAll(rules, Now).IsAcquired.Should().BeTrue();
    }

    /// <summary>
    /// The reported budget is the scope closest to refusing. Reporting the roomiest would have a
    /// client pace itself against a limit that is not the one about to stop it.
    /// </summary>
    [Fact]
    public void TryAcquireAll_ReportsTheTightestScope()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var rules = new RateLimitRule[]
        {
            new(RateLimitScope.Tenant, "t:acme", new RateLimitPolicy(1000, 0, 0)),
            new(RateLimitScope.ApiKeyModel, "km:key|gpt-4", new RateLimitPolicy(4, 0, 0)),
        };

        var result = store.TryAcquireAll(rules, Now);

        result.IsAcquired.Should().BeTrue();
        result.Scope.Should().Be(RateLimitScope.ApiKeyModel);
        result.Limit.Should().Be(4);
        result.Remaining.Should().Be(3);
    }

    /// <summary>
    /// A request refused by its narrowest scope must not have spent the wider ones. Without the
    /// refund, one over-limit key would eventually rate-limit its whole tenant — and every model
    /// that tenant uses — purely by retrying.
    /// </summary>
    [Fact]
    public void TryAcquireAll_WhenALaterScopeRefuses_RefundsTheEarlierOnes()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var tenant = new RateLimitPolicy(100, 0, 0);
        var rules = new RateLimitRule[]
        {
            new(RateLimitScope.Tenant, "t:acme", tenant),
            new(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(1, 0, 0)),
        };

        store.TryAcquireAll(rules, Now).IsAcquired.Should().BeTrue();

        // The model bucket is now empty; hammer it well past its limit.
        for (var i = 0; i < 50; i++)
        {
            var refused = store.TryAcquireAll(rules, Now);
            refused.IsAcquired.Should().BeFalse();
            refused.Scope.Should().Be(RateLimitScope.Model, "the model rule is the one that ran out");
        }

        // 100 tenant tokens, exactly one of which was spent: the 50 refusals cost it nothing.
        store.PeekRequest("t:acme", tenant, Now).Remaining.Should().Be(99);
    }

    /// <summary>
    /// The refund is capped at capacity, so it can never hand a partition more than its tier — even
    /// if a caller refunded a rule it had not acquired.
    /// </summary>
    [Fact]
    public void RefundAll_NeverExceedsCapacity()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(5, 0, 0);
        var rules = new RateLimitRule[] { new(RateLimitScope.Tenant, "t:acme", policy) };

        store.TryAcquireAll(rules, Now).IsAcquired.Should().BeTrue();

        for (var i = 0; i < 20; i++)
        {
            store.RefundAll(rules, Now);
        }

        store.PeekRequest("t:acme", policy, Now).Remaining.Should().Be(5);
    }

    /// <summary>An empty rule set enforces nothing and must not fabricate a budget to report.</summary>
    [Fact]
    public void TryAcquireAll_WithNoEnforcingRules_AdmitsWithNoBudget()
    {
        var store = new InMemoryDistributedRateLimitStore();

        var result = store.TryAcquireAll([], Now);

        result.IsAcquired.Should().BeTrue();
        result.Limit.Should().BeNull();
    }

    /// <summary>
    /// Concurrency composes the same way: a slot is taken in every capping scope, and if a later one
    /// is full the earlier slots are given straight back. A leaked slot here is permanent — the
    /// scope's capacity shrinks for the life of the process.
    /// </summary>
    [Fact]
    public void TryAcquireStreamSlots_WhenALaterScopeIsFull_ReleasesTheEarlierSlots()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var rules = new RateLimitRule[]
        {
            new(RateLimitScope.Tenant, "t:acme", new RateLimitPolicy(0, 0, 10)),
            new(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(0, 0, 1)),
        };

        store.TryAcquireStreamSlots(rules, out var first).IsAcquired.Should().BeTrue();
        first.Count.Should().Be(2);

        var refused = store.TryAcquireStreamSlots(rules, out var none);
        refused.IsAcquired.Should().BeFalse();
        refused.Scope.Should().Be(RateLimitScope.Model);
        none.Count.Should().Be(0);

        // The tenant scope still has 9 free: the refused attempt gave back the slot it had taken.
        store.TryAcquireStreamSlot("t:acme", new RateLimitPolicy(0, 0, 2)).IsAcquired.Should()
            .BeTrue("only one tenant slot is held, so a cap of 2 still has room");
    }

    [Fact]
    public void ReleaseStreamSlots_GivesBackEveryScope()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var rules = new RateLimitRule[]
        {
            new(RateLimitScope.Tenant, "t:acme", new RateLimitPolicy(0, 0, 1)),
            new(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(0, 0, 1)),
        };

        store.TryAcquireStreamSlots(rules, out var held).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlots(rules, out _).IsAcquired.Should().BeFalse();

        store.ReleaseStreamSlots(held);

        store.TryAcquireStreamSlots(rules, out _).IsAcquired.Should()
            .BeTrue("both scopes were released, not just the first");
    }

    /// <summary>
    /// Concurrent traffic against a composed rule set must never admit more than the tightest scope
    /// allows, and the refunds must not leak tokens into the wider ones.
    /// </summary>
    [Fact]
    public async Task TryAcquireAll_Concurrent_AdmitsExactlyTheTightestLimit()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var tenant = new RateLimitPolicy(10_000, 0, 0);
        var rules = new RateLimitRule[]
        {
            new(RateLimitScope.Tenant, "t:acme", tenant),
            new(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(40, 0, 0)),
        };

        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (store.TryAcquireAll(rules, Now).IsAcquired)
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })));

        admitted.Should().Be(40);
        store.PeekRequest("t:acme", tenant, Now).Remaining.Should().Be(
            10_000 - 40,
            "every refused attempt refunded the tenant token it had taken");
    }

    private static RateLimitRule[] Rules(params (string Key, int Rpm)[] entries) =>
        [.. entries.Select(e => new RateLimitRule(
            RateLimitScope.Tenant,
            e.Key,
            new RateLimitPolicy(e.Rpm, 0, 0)))];
}
