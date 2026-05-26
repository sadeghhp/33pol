using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Billing.Webhooks;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Webhooks;

public sealed class DailyUsageWebhookPublisherTests
{
    [Fact]
    public async Task DispatchYesterdayAsync_WrongHour_DoesNotDispatch()
    {
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var publisher = CreatePublisher(webhooks, utcHour: 2);

        await publisher.DispatchYesterdayAsync(new DateTime(2026, 5, 27, 3, 0, 0, DateTimeKind.Utc));

        await webhooks.DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchYesterdayAsync_MatchingHour_DispatchesGroupedRollups()
    {
        var tenantId = Guid.NewGuid();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(yesterday, yesterday, null, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(yesterday, tenantId, "gpt-4o", null, 10, 5, 0.05m, 1),
                new DailyUsageRollupRecord(yesterday, tenantId, "gpt-4o-mini", null, 20, 10, 0.10m, 2),
            ]);

        var publisher = CreatePublisher(webhooks, utcHour: 4, rollups: rollups);
        await publisher.DispatchYesterdayAsync(new DateTime(2026, 5, 27, 4, 30, 0, DateTimeKind.Utc));

        await webhooks.Received(1).DispatchAsync(
            "usage.daily",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    private static DailyUsageWebhookPublisher CreatePublisher(
        IBillingWebhookDispatcher webhooks,
        int utcHour,
        IDailyUsageRollupRepository? rollups = null)
    {
        rollups ??= Substitute.For<IDailyUsageRollupRepository>();
        return new DailyUsageWebhookPublisher(
            rollups,
            webhooks,
            Options.Create(new BillingOptions { DailyWebhookUtcHour = utcHour, DefaultCurrency = "USD" }),
            new BillingDailyUsageWebhookTracker());
    }
}
