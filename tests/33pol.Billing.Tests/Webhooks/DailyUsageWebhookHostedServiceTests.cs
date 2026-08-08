using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Billing.Webhooks;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Webhooks;

public sealed class DailyUsageWebhookHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenWebhookNotConfigured_CompletesWithoutDispatch()
    {
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var service = CreateService(webhooks, configured: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        await webhooks.DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<Action?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenConfigured_DispatchesOnPollTick()
    {
        var tenantId = Guid.NewGuid();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var webhooks = Substitute.For<IBillingWebhookDispatcher>();
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(yesterday, yesterday, null, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(yesterday, tenantId, "gpt-4o", null, 1, 1, 0.5m, 1),
            ]);

        var service = CreateService(
            webhooks,
            configured: true,
            rollups: rollups,
            pollIntervalSeconds: 1,
            dailyWebhookUtcHour: DateTime.UtcNow.Hour);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await service.StartAsync(cts.Token);
        await Task.Delay(1500, CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await webhooks.Received().DispatchAsync(
            "usage.daily",
            Arg.Any<object>(),
            Arg.Any<Action?>(),
            Arg.Any<CancellationToken>());
    }

    private static DailyUsageWebhookHostedService CreateService(
        IBillingWebhookDispatcher webhooks,
        bool configured,
        IDailyUsageRollupRepository? rollups = null,
        int pollIntervalSeconds = 900,
        int dailyWebhookUtcHour = 1)
    {
        rollups ??= Substitute.For<IDailyUsageRollupRepository>();
        var services = new ServiceCollection();
        services.AddSingleton(rollups);
        services.AddSingleton(webhooks);
        services.AddSingleton(Options.Create(new BillingOptions
        {
            DailyWebhookPollIntervalSeconds = pollIntervalSeconds,
            DailyWebhookUtcHour = dailyWebhookUtcHour,
        }));
        services.AddSingleton<BillingDailyUsageWebhookTracker>();
        services.AddScoped<DailyUsageWebhookPublisher>();
        var provider = services.BuildServiceProvider();

        return new DailyUsageWebhookHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BillingWebhookOptions
            {
                EndpointUrl = configured ? "https://hooks.example/33pol" : null,
                Secret = configured ? "secret" : string.Empty,
            }),
            NullLogger<DailyUsageWebhookHostedService>.Instance);
    }
}
