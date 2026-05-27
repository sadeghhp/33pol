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
    public async Task DispatchYesterdayAsync_WhenHourMatches_SendsPerTenant()
    {
        var tenantId = Guid.NewGuid();
        var utcNow = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var yesterday = DateOnly.FromDateTime(utcNow).AddDays(-1);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(yesterday, yesterday, null, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(yesterday, tenantId, "gpt-4o", null, 10, 5, 1.5m, 2),
            ]);

        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var publisher = new DailyUsageWebhookPublisher(
            rollups,
            webhooks,
            Options.Create(new BillingOptions { DailyWebhookUtcHour = 12, DefaultCurrency = "EUR" }),
            new BillingDailyUsageWebhookTracker());

        await publisher.DispatchYesterdayAsync(utcNow);

        await webhooks.Received(1).DispatchAsync(
            "usage.daily",
            Arg.Is<object>(p => p.ToString()!.Contains("EUR")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchYesterdayAsync_WrongHour_SkipsDispatch()
    {
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var publisher = new DailyUsageWebhookPublisher(
            rollups,
            webhooks,
            Options.Create(new BillingOptions { DailyWebhookUtcHour = 3 }),
            new BillingDailyUsageWebhookTracker());

        await publisher.DispatchYesterdayAsync(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));

        await rollups.DidNotReceive().GetRollupsAsync(
            Arg.Any<DateOnly?>(),
            Arg.Any<DateOnly?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }
}
