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
        BillingDailyUsageWebhookTracker? dailyTracker = null) =>
        new(
            billingEvents,
            rollups,
            new DailyUsageRollupAggregator(),
            rateCards ?? Substitute.For<IRateCardRepository>(),
            new RateCardCostCalculator(),
            budgets ?? Substitute.For<IBudgetRepository>(),
            webhooks ?? Substitute.For<IBillingWebhookDispatcher>(),
            warningTracker ?? new BillingBudgetWarningTracker(),
            dailyTracker ?? new BillingDailyUsageWebhookTracker(),
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
        await rollups.Received(1).UpsertRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupRecord>>(list => list.Count == 1 && list[0].RequestCount == 1),
            Arg.Any<CancellationToken>());
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
        await rollups.Received(2).UpsertRollupsAsync(
            Arg.Any<IReadOnlyList<DailyUsageRollupRecord>>(),
            Arg.Any<CancellationToken>());
    }
}
