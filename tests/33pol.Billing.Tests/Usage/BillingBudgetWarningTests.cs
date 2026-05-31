using Microsoft.Extensions.DependencyInjection;
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

public sealed class BillingBudgetWarningTests
{
    [Fact]
    public async Task PersistAsync_SpendAt80Percent_DispatchesQuotaWarning()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var handler = CreateHandler(tenantId, today, spend: 80m, limit: 100m, webhooks: webhooks);

        await handler.PersistAsync(CreateUsageEvent(tenantId, "req-warn-80"));

        await webhooks.Received(1).DispatchAsync(
            "quota.warning",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_SpendBelow80Percent_DoesNotDispatchQuotaWarning()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var handler = CreateHandler(tenantId, today, spend: 79.99m, limit: 100m, webhooks: webhooks);

        await handler.PersistAsync(CreateUsageEvent(tenantId, "req-warn-below"));

        await webhooks.DidNotReceive().DispatchAsync(
            "quota.warning",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_SoftBudgetOnly_DoesNotBlockViaEnforcement()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(),
                    tenantId,
                    "Soft only",
                    100m,
                    "USD",
                    0.8m,
                    HardStopEnabled: false,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 150m, 1),
            ]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);
        var result = await service.CheckBeforeForwardAsync(tenantId.ToString());

        result.IsAllowed.Should().BeTrue();
    }

    private static BillingUsagePersistenceHandler CreateHandler(
        Guid tenantId,
        DateOnly today,
        decimal spend,
        decimal limit,
        IBillingWebhookDispatcher webhooks)
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(today, today, tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, spend, 1)]);
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, spend, 1)]);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(),
                    tenantId,
                    "Monthly",
                    limit,
                    "USD",
                    0.8m,
                    false,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        return new BillingUsagePersistenceHandler(
            billingEvents,
            rollups,
            new DailyUsageRollupAggregator(),
            Substitute.For<IRateCardRepository>(),
            new RateCardCostCalculator(),
            budgets,
            webhooks,
            new BillingBudgetWarningTracker(),
            new BillingDailyUsageWebhookTracker(),
            Substitute.For<IApiKeyLastUsedTracker>(),
            Options.Create(new BillingOptions()));
    }

    private static UsageEvent CreateUsageEvent(Guid tenantId, string requestId) =>
        new()
        {
            RequestId = requestId,
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        };
}

internal static class BillingBudgetEnforcementServiceTestsHelper
{
    internal static BillingBudgetEnforcementService CreateService(
        IBudgetRepository budgets,
        IDailyUsageRollupRepository rollups)
    {
        var services = new ServiceCollection();
        services.AddSingleton(budgets);
        services.AddSingleton(rollups);
        var provider = services.BuildServiceProvider();
        return new BillingBudgetEnforcementService(provider.GetRequiredService<IServiceScopeFactory>());
    }
}
