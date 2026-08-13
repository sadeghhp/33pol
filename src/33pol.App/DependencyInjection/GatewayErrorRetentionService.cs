using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Trims stored errors to the configured age and row limits.
/// </summary>
/// <remarks>
/// This deletes evidence, so the effective retention is logged at startup rather than left implicit
/// — an operator looking for last month's incident should learn it is gone from a log line, not
/// from an empty search result.
/// </remarks>
internal sealed class GatewayErrorRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<GatewayErrorTrackingOptions> options,
    ILogger<GatewayErrorRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "Error retention active: records older than {RetentionDays} days are deleted, and the table is trimmed to {MaxRows} rows.",
            settings.RetentionDays,
            settings.MaxRows);

        try
        {
            // Let startup settle before the first pass; migrations and the stats restore are both
            // touching the same database right now.
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            var interval = TimeSpan.FromMinutes(Math.Max(1, settings.PruneIntervalMinutes));
            using var timer = new PeriodicTimer(interval);

            do
            {
                await PruneAsync(settings, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task PruneAsync(GatewayErrorTrackingOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var archive = scope.ServiceProvider.GetRequiredService<IGatewayErrorArchive>();

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Max(1, settings.RetentionDays));
            var removed = await archive.PruneAsync(cutoff, settings.MaxRows, cancellationToken)
                .ConfigureAwait(false);

            if (removed > 0)
            {
                logger.LogInformation("Pruned {Count} error records past the retention window.", removed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retention falling behind is not worth failing the process over; the row cap is a
            // guideline, not an invariant.
            logger.LogWarning(ex, "Error retention pass failed; it will be retried on the next tick.");
        }
    }
}
