using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

public sealed class DailyUsageWebhookHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingWebhookOptions> webhookOptions,
    ILogger<DailyUsageWebhookHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!webhookOptions.Value.IsConfigured)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var billingOptions = scope.ServiceProvider.GetRequiredService<IOptions<BillingOptions>>().Value;
                var pollSeconds = Math.Max(1, billingOptions.DailyWebhookPollIntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken).ConfigureAwait(false);

                var publisher = scope.ServiceProvider.GetRequiredService<DailyUsageWebhookPublisher>();
                await publisher
                    .DispatchYesterdayAsync(DateTime.UtcNow, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Daily usage webhook loop failed");
            }
        }
    }
}
