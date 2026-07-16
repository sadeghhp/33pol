using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Observability.Runtime;
using Pol33.Policy.Quotas;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Snapshots the in-memory dashboard counters (<see cref="GatewayRuntimeState"/>) and monthly quota
/// usage (<see cref="InMemoryQuotaService"/>) to the database on an interval and on graceful
/// shutdown, and restores them on startup — so both survive gateway container recreation. The DB is
/// the durable copy; the in-memory objects stay the hot path so there is no write per request.
///
/// Registered only when a database connection string is configured. Single-instance semantics: one
/// gateway owns the single snapshot row (consistent with the runtime state / budget-reservation
/// ledger). After an ungraceful kill, up to one flush interval of increments is lost.
///
/// Registered after <c>AddGatewayPersistence</c>, so its startup restore runs after database
/// migrations. Like the other startup hosted services (e.g. the DB initializer), the restore races
/// the HTTP server's own hosted service, so there is a sub-second startup window in which a request
/// may be recorded just before <c>Hydrate</c> replaces the counters (cosmetic) or a quota check may
/// see not-yet-restored usage. Bounded and acceptable for telemetry-grade counters.
/// </summary>
internal sealed class GatewayStatsSnapshotService(
    IServiceScopeFactory scopeFactory,
    GatewayRuntimeState runtimeState,
    IQuotaUsageSnapshotSource quotaUsageSource,
    IOptions<GatewayStatsPersistenceOptions> options,
    ILogger<GatewayStatsSnapshotService> logger) : IHostedService
{
    private Task? _flushLoop;
    private CancellationTokenSource? _cts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestoreAsync(cancellationToken).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushLoop = RunFlushLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_flushLoop is not null)
        {
            try
            {
                await _flushLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        // Final flush so nothing since the last tick is lost on a graceful stop.
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var statsStore = scope.ServiceProvider.GetRequiredService<IGatewayStatsSnapshotStore>();
            var quotaStore = scope.ServiceProvider.GetRequiredService<IQuotaUsageSnapshotStore>();

            var stats = await statsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (stats is not null)
            {
                runtimeState.Hydrate(stats);
            }

            var usage = await quotaStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (usage.Count > 0)
            {
                quotaUsageSource.HydrateUsage(usage);
            }

            logger.LogInformation(
                "Restored dashboard stats snapshot ({RecentCount} recent requests) and {QuotaPartitions} quota partitions from the database.",
                stats?.Recent.Count ?? 0,
                usage.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telemetry, not critical path: start with empty counters rather than block startup.
            logger.LogWarning(ex, "Failed to restore dashboard stats snapshot; starting with fresh counters.");
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, options.Value.FlushIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var statsStore = scope.ServiceProvider.GetRequiredService<IGatewayStatsSnapshotStore>();
            var quotaStore = scope.ServiceProvider.GetRequiredService<IQuotaUsageSnapshotStore>();

            await statsStore.SaveAsync(runtimeState.Export(), cancellationToken).ConfigureAwait(false);
            await quotaStore.SaveAsync(quotaUsageSource.ExportUsage(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to flush dashboard stats snapshot to the database.");
        }
    }
}
