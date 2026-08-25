using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection.Overview;

/// <summary>
/// Keeps the slow Overview sections warm so the summary's Attention list can judge budgets,
/// reconciliation and control-plane state even when nobody has the FinOps card open. Starts after a
/// short delay so it never competes with migrations and registry load at boot.
/// </summary>
internal sealed class GatewayOverviewRefreshHostedService(
    GatewayOverviewSectionService sections,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<GatewayOverviewRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Value.Overview.SlowSectionTtlSeconds));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(ttl, timeProvider);
            do
            {
                try
                {
                    await sections.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Overview refresh failed; will retry on the next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
