using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Executes clear-all-errors across every place an error total is kept: the record store, the
/// in-memory counters, the recent-request feed, and the persisted snapshot.
/// </summary>
/// <remarks>
/// Lives in the composition root because it is the only layer allowed to see both observability and
/// persistence — the admin endpoints may reference Core alone.
/// <para>
/// Order matters. Buffered writes are discarded and the store emptied first, so a record captured
/// moments ago cannot land after the wipe. The counters are reset next. Only then is the snapshot
/// rewritten, and it is rewritten from the <em>already-reset</em> export rather than deleted:
/// deleting the row would take total requests, latency and the whole recent feed with it, which is
/// far more than the operator asked for. <c>scope=all</c> is the explicit opt-in to that.
/// </para>
/// </remarks>
internal sealed class GatewayErrorAdminService(
    IGatewayErrorStore errorStore,
    GatewayRuntimeState runtimeState,
    GatewayStatsFlushCoordinator flushCoordinator,
    IServiceScopeFactory scopeFactory,
    ILogger<GatewayErrorAdminService> logger) : IGatewayErrorAdminService
{
    public async Task<GatewayErrorClearResult> ClearAllAsync(
        GatewayErrorClearScope scope,
        CancellationToken cancellationToken = default)
    {
        using var _ = await flushCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // Counters first, store second. The in-memory counters are the source the snapshot is
        // written from, so they have to be reset before it is saved — and resetting them before
        // the store means a failure to delete stored rows leaves "counter 0, rows present", which
        // the response reports, rather than "rows gone, counter intact" thrown as a 500 with the
        // in-memory buffer already wiped.
        var (totalErrorsCleared, recentRowsRemoved) = runtimeState.ResetErrors();
        if (scope == GatewayErrorClearScope.AllCounters)
        {
            runtimeState.ResetAll();
        }

        var recordsDeleted = 0;
        var archiveCleared = true;
        try
        {
            recordsDeleted = await errorStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            archiveCleared = false;
            logger.LogError(ex, "Reset the error counters but failed to delete the stored error records.");
        }

        var snapshotRewritten = false;
        var databaseAvailable = false;

        await using (var serviceScope = scopeFactory.CreateAsyncScope())
        {
            var snapshotStore = serviceScope.ServiceProvider.GetService<IGatewayStatsSnapshotStore>();
            if (snapshotStore is not null)
            {
                databaseAvailable = true;
                try
                {
                    // Both scopes save the live export; ResetAll above is what makes the
                    // AllCounters export empty. Saving a hand-built empty snapshot instead would
                    // let the two diverge.
                    await snapshotStore.SaveAsync(runtimeState.Export(), cancellationToken)
                        .ConfigureAwait(false);

                    snapshotRewritten = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Reported rather than swallowed: an unrewritten snapshot means the next restart
                    // resurrects the counts, and the operator needs to know that now.
                    logger.LogError(
                        ex,
                        "Cleared errors in memory but failed to rewrite the persisted counter snapshot. " +
                        "The old totals will be restored if the gateway restarts.");
                }
            }
        }

        logger.LogInformation(
            "Cleared {Records} error records and {TotalErrors} counted errors ({RecentRows} feed rows removed, scope {Scope}).",
            recordsDeleted,
            totalErrorsCleared,
            recentRowsRemoved,
            scope);

        return new GatewayErrorClearResult
        {
            RecordsDeleted = recordsDeleted,
            ArchiveCleared = archiveCleared,
            RecentRequestRowsRemoved = recentRowsRemoved,
            TotalErrorsCleared = totalErrorsCleared,
            SnapshotRewritten = snapshotRewritten,
            DatabaseAvailable = databaseAvailable,
        };
    }
}
