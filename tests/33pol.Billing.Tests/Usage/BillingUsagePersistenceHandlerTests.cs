using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Aggregates;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUsagePersistenceHandlerTests
{
    private static BillingUsagePersistenceHandler CreateHandler(
        IBillingEventRepository billingEvents,
        IDailyUsageRollupRepository rollups,
        IRateCardRepository? rateCards = null,
        IBudgetRepository? budgets = null,
        IBillingWebhookDispatcher? webhooks = null,
        BillingBudgetWarningTracker? warningTracker = null,
        BillingDailyUsageWebhookTracker? dailyTracker = null,
        IApiKeyLastUsedTracker? lastUsedTracker = null) =>
        new(
            billingEvents,
            rollups,
            rateCards ?? Substitute.For<IRateCardRepository>(),
            new RateCardCostCalculator(),
            budgets ?? Substitute.For<IBudgetRepository>(),
            webhooks ?? Substitute.For<IBillingWebhookDispatcher>(),
            warningTracker ?? new BillingBudgetWarningTracker(),
            dailyTracker ?? new BillingDailyUsageWebhookTracker(),
            new BillingUnpricedModelTracker(),
            lastUsedTracker ?? Substitute.For<IApiKeyLastUsedTracker>(),
            new BudgetReservationLedger(TimeSpan.FromMinutes(2)),
            NullLogger<BillingUsagePersistenceHandler>.Instance,
            Options.Create(new BillingOptions { DefaultCurrency = "USD" }));

    [Fact]
    public async Task PersistAsync_NewEvent_AppendsAndUpsertsRollup()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-1",
            TenantId = Guid.NewGuid().ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 10,
            CompletionTokens = 5,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());

        // Rollups are now applied as an atomic additive delta rather than read, added to in memory
        // and written back as an absolute total, which could lose a concurrent writer's usage.
        await rollups.Received(1).IncrementRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupDelta>>(list =>
                list.Count == 1 &&
                list[0].RequestCount == 1 &&
                list[0].PromptTokens == 10 &&
                list[0].CompletionTokens == 5),
            Arg.Any<CancellationToken>());
        await rollups.DidNotReceive().UpsertRollupsAsync(
            Arg.Any<IReadOnlyList<DailyUsageRollupRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_WithApiKeyId_TouchesLastUsed()
    {
        var apiKeyId = Guid.NewGuid();
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var lastUsed = Substitute.For<IApiKeyLastUsedTracker>();
        var handler = CreateHandler(billingEvents, rollups, lastUsedTracker: lastUsed);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-touch",
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = apiKeyId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 10,
            CompletionTokens = 5,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await lastUsed.Received(1).TouchAsync(apiKeyId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_DuplicateRequestId_SkipsRollup()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var handler = CreateHandler(billingEvents, rollups);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "dup",
            ModelId = "m1",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
        });

        await rollups.DidNotReceive().UpsertRollupsAsync(Arg.Any<IReadOnlyList<DailyUsageRollupRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_WithRateCard_StoresCostsOnEvent()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        BillingEventRecord? captured = null;
        billingEvents.TryAppendAsync(Arg.Do<BillingEventRecord>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync("gpt-4o", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new RateCardRecord(
                Guid.NewGuid(),
                "default",
                "Default",
                "gpt-4o",
                1m,
                2m,
                "USD",
                DateTimeOffset.UtcNow.AddDays(-1),
                null,
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        var handler = CreateHandler(billingEvents, rollups, rateCards);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-cost",
            ModelId = "gpt-4o",
            PromptTokens = 1_000_000,
            CompletionTokens = 0,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        captured.Should().NotBeNull();
        captured!.InputCost.Should().Be(1m);
        captured.TotalCost.Should().Be(1m);
    }

    [Fact]
    public void GetPeriodStart_MidMonth_ReturnsStartOfCurrentPeriod()
    {
        var today = new DateOnly(2026, 5, 26);
        BillingUsagePersistenceHandler.GetPeriodStart(today, 1)
            .Should().Be(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public async Task PersistAsync_WithTenant_DispatchesUsageDailyWebhook()
    {
        var tenantId = Guid.NewGuid();
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    tenantId,
                    "gpt-4o",
                    "eng",
                    100,
                    50,
                    0.15m,
                    2),
            ]);

        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var handler = CreateHandler(billingEvents, rollups, webhooks: webhooks);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-daily",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            CostCenter = "eng",
            PromptTokens = 100,
            CompletionTokens = 50,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await webhooks.Received(1).DispatchAsync(
            "usage.daily",
            Arg.Is<object>(payload => payload.ToString()!.Contains("totalCost")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_BudgetThresholdCrossed_DispatchesQuotaWarningOnce()
    {
        var tenantId = Guid.NewGuid();
        var budgetId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(today, today, tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 90m, 1),
            ]);
        rollups.GetRollupsAsync(
                Arg.Any<DateOnly?>(),
                Arg.Any<DateOnly?>(),
                tenantId,
                Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 90m, 1),
            ]);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    budgetId,
                    tenantId,
                    "Monthly cap",
                    100m,
                    "USD",
                    0.8m,
                    false,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var tracker = new BillingBudgetWarningTracker();
        var handler = CreateHandler(billingEvents, rollups, budgets: budgets, webhooks: webhooks, warningTracker: tracker);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-budget",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-budget-2",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await webhooks.Received(1).DispatchAsync(
            "quota.warning",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_SecondEventSameDay_SendsUsageDailyOnce()
    {
        var tenantId = Guid.NewGuid();
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    tenantId,
                    "gpt-4o",
                    null,
                    1,
                    1,
                    0.01m,
                    1),
            ]);

        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var dailyTracker = new BillingDailyUsageWebhookTracker();
        var handler = CreateHandler(billingEvents, rollups, webhooks: webhooks, dailyTracker: dailyTracker);

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-1",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-2",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await webhooks.Received(1).DispatchAsync(
            "usage.daily",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistBatchAsync_MultipleEvents_UpsertsEachRollup()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups);

        await handler.PersistBatchAsync([
            new UsageEvent
            {
                RequestId = "batch-1",
                TenantId = Guid.NewGuid().ToString(),
                ModelId = "gpt-4o",
                PromptTokens = 1,
                CompletionTokens = 1,
                DurationMs = 1,
            },
            new UsageEvent
            {
                RequestId = "batch-2",
                TenantId = Guid.NewGuid().ToString(),
                ModelId = "gpt-4o-mini",
                PromptTokens = 2,
                CompletionTokens = 2,
                DurationMs = 1,
            },
        ]);

        await billingEvents.Received(2).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());

        // One rollup call for the whole batch, carrying one delta per distinct bucket — not one
        // database round-trip per event.
        await rollups.Received(1).IncrementRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupDelta>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Events sharing a bucket must collapse into one delta whose totals equal the sum of the
    /// individual events — the batching must not change what is billed.
    /// </summary>
    [Fact]
    public async Task PersistBatchAsync_EventsInTheSameBucket_CollapseIntoOneExactDelta()
    {
        var tenantId = Guid.NewGuid();
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var handler = CreateHandler(billingEvents, rollups);

        var timestamp = DateTimeOffset.UtcNow;
        await handler.PersistBatchAsync(Enumerable.Range(1, 5).Select(i => new UsageEvent
        {
            RequestId = $"same-bucket-{i}",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = i,
            CompletionTokens = i * 2,
            DurationMs = 1,
            TimestampUtc = timestamp,
        }).ToList());

        await rollups.Received(1).IncrementRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupDelta>>(list =>
                list.Count == 1 &&
                list[0].RequestCount == 5 &&
                list[0].PromptTokens == 15 &&
                list[0].CompletionTokens == 30),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Repository call counts must scale with the number of distinct groups, not with the number of
    /// events. A 100-event batch used to issue roughly 600 round-trips.
    /// </summary>
    [Fact]
    public async Task PersistBatchAsync_CallCountsScaleWithGroupsNotEvents()
    {
        var tenantId = Guid.NewGuid();
        var apiKeyId = Guid.NewGuid();

        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var rateCards = Substitute.For<IRateCardRepository>();
        var lastUsed = Substitute.For<IApiKeyLastUsedTracker>();
        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var handler = CreateHandler(
            billingEvents, rollups, rateCards, budgets, lastUsedTracker: lastUsed);

        var timestamp = DateTimeOffset.UtcNow;
        await handler.PersistBatchAsync(Enumerable.Range(0, 100).Select(i => new UsageEvent
        {
            RequestId = $"scale-{i}",
            TenantId = tenantId.ToString(),
            ApiKeyId = apiKeyId.ToString(),
            // Two distinct models => two rollup buckets, whatever the event count.
            ModelId = i % 2 == 0 ? "gpt-4o" : "gpt-4o-mini",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = timestamp,
        }).ToList());

        // One rate-card lookup per distinct model.
        await rateCards.Received(2).GetActiveForModelAsync(
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        // One rollup write for the whole batch, two deltas inside it.
        await rollups.Received(1).IncrementRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupDelta>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());

        // One last-used touch per distinct api key, and one budget scan per distinct tenant.
        await lastUsed.Received(1).TouchAsync(apiKeyId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await budgets.Received(1).GetByTenantAsync(tenantId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Buckets must stay isolated: different tenants, models, dates and cost centres each get their
    /// own delta rather than being merged.
    /// </summary>
    [Fact]
    public async Task PersistBatchAsync_MixedBuckets_RemainIsolated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var today = DateTimeOffset.UtcNow;
        var yesterday = today.AddDays(-1);

        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var handler = CreateHandler(billingEvents, rollups);

        await handler.PersistBatchAsync([
            NewEvent("a", tenantA, "gpt-4o", "cc-1", today),
            NewEvent("b", tenantB, "gpt-4o", "cc-1", today),
            NewEvent("c", tenantA, "gpt-4o-mini", "cc-1", today),
            NewEvent("d", tenantA, "gpt-4o", "cc-2", today),
            NewEvent("e", tenantA, "gpt-4o", "cc-1", yesterday),
        ]);

        await rollups.Received(1).IncrementRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupDelta>>(list =>
                list.Count == 5 && list.All(d => d.RequestCount == 1)),
            Arg.Any<CancellationToken>());
    }

    private static UsageEvent NewEvent(
        string requestId,
        Guid tenantId,
        string modelId,
        string costCenter,
        DateTimeOffset timestamp) =>
        new()
        {
            RequestId = requestId,
            TenantId = tenantId.ToString(),
            ModelId = modelId,
            CostCenter = costCenter,
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = timestamp,
        };
}
