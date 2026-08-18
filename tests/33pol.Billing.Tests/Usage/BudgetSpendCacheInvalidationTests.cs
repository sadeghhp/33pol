using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// The persisted-spend cache and the reservation ledger together must always account for every
/// request's cost. The usage writer releases reservations as soon as a batch's rollup increment
/// commits, so it must invalidate the tenant's cached spend first — otherwise, until the cache
/// TTL elapsed, the batch's cost was held by neither and concurrent requests were admitted against
/// stale headroom.
/// </summary>
public sealed class BudgetSpendCacheInvalidationTests
{
    [Fact]
    public void Invalidate_BumpsGeneration_SoCachedValueIsNoLongerServed()
    {
        var cache = new BudgetSpendCache(new MemoryCache(new MemoryCacheOptions()));
        var tenant = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        cache.Set(tenant, today, today, 42m, TimeSpan.FromMinutes(5));
        cache.TryGet(tenant, today, today, out var before).Should().BeTrue();
        before.Should().Be(42m);

        cache.Invalidate(tenant);

        cache.TryGet(tenant, today, today, out _).Should().BeFalse();
        cache.GetGeneration(tenant).Should().Be(1);
    }

    [Fact]
    public void Invalidate_IsScopedToTheTenant()
    {
        var cache = new BudgetSpendCache(new MemoryCache(new MemoryCacheOptions()));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        cache.Set(tenantA, today, today, 1m, TimeSpan.FromMinutes(5));
        cache.Set(tenantB, today, today, 2m, TimeSpan.FromMinutes(5));

        cache.Invalidate(tenantA);

        cache.TryGet(tenantA, today, today, out _).Should().BeFalse();
        cache.TryGet(tenantB, today, today, out var b).Should().BeTrue();
        b.Should().Be(2m);
    }

    /// <summary>
    /// End to end: enforcement caches spend at 50; a batch worth 50 more persists; the reservation
    /// is released. The very next enforcement read must see 100 (from the rollups), not the cached
    /// 50, so a hard stop at 100 engages immediately.
    /// </summary>
    [Fact]
    public async Task PersistBatch_InvalidatesCachedSpendBeforeReleasingReservations()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var persistedSpend = 50m;

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns(_ => [new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, persistedSpend, 1)]);
        rollups.IncrementRollupsAsync(Arg.Any<IReadOnlyList<DailyUsageRollupDelta>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                persistedSpend += call.Arg<IReadOnlyList<DailyUsageRollupDelta>>().Sum(d => d.TotalCost);
                return Task.CompletedTask;
            });

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(), tenantId, "Cap", 100m, "USD", 0.8m,
                    HardStopEnabled: true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        var rateCard = new RateCardRecord(
            Guid.NewGuid(), "card", "Card", "gpt-4o",
            InputPricePerMillionTokens: 50_000_000m, // 1 token = 50 currency units
            OutputPricePerMillionTokens: 0m,
            "USD", DateTimeOffset.UtcNow.AddDays(-1), null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync("gpt-4o", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);

        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var spendCache = new BudgetSpendCache(new MemoryCache(new MemoryCacheOptions()));
        var enforcement = BillingBudgetEnforcementServiceTestsHelper.CreateService(
            budgets, rollups, ledger, rateCards, spendCache);

        // Warm the cache at 50 and hold a reservation for the in-flight request.
        ledger.TryReserve("req-1", tenantId, 50m, 100m).Should().BeTrue();
        (await enforcement.CheckBeforeForwardAsync(tenantId.ToString())).IsAllowed.Should().BeFalse("50 cached + 50 reserved");

        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new BillingUsagePersistenceHandler(
            billingEvents,
            rollups,
            rateCards,
            new RateCardCostCalculator(),
            budgets,
            Substitute.For<IBillingWebhookDispatcher>(),
            new BillingBudgetWarningTracker(),
            new BillingUnpricedModelTracker(),
            Substitute.For<IApiKeyLastUsedTracker>(),
            ledger,
            NullLogger<BillingUsagePersistenceHandler>.Instance,
            recentRequests: null,
            spendCache: spendCache);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-1",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 0,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        // The reservation is gone AND the cache was invalidated: the next read sums the rollups (100).
        ledger.GetOutstanding(tenantId).Should().Be(0m);
        spendCache.GetGeneration(tenantId).Should().Be(1);
        (await enforcement.CheckBeforeForwardAsync(tenantId.ToString())).IsAllowed
            .Should().BeFalse("persisted spend is now 100 and must be observed immediately, not after the cache TTL");
    }
}
