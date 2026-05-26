using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Billing.Webhooks;
using Pol33.Core.Abstractions;
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
            Arg.Any<CancellationToken>());
    }

    private static DailyUsageWebhookHostedService CreateService(
        IBillingWebhookDispatcher webhooks,
        bool configured)
    {
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var services = new ServiceCollection();
        services.AddSingleton(rollups);
        services.AddSingleton(webhooks);
        services.AddSingleton(Options.Create(new BillingOptions { DailyWebhookUtcHour = DateTime.UtcNow.Hour }));
        services.AddSingleton<BillingDailyUsageWebhookTracker>();
        var provider = services.BuildServiceProvider();

        return new DailyUsageWebhookHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BillingWebhookOptions
            {
                EndpointUrl = configured ? "https://hooks.example/33pol" : null,
                Secret = configured ? "secret" : string.Empty,
            }),
            provider.GetRequiredService<BillingDailyUsageWebhookTracker>(),
            NullLogger<DailyUsageWebhookHostedService>.Instance);
    }
}
