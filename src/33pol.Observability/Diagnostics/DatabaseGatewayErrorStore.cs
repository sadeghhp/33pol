using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// The error store used when a database is configured: writes go through the in-memory buffer and
/// the batch writer, reads come from the archive.
/// </summary>
/// <remarks>
/// Reads flush pending writes first and then query the archive exclusively — no merging of buffer
/// and database results. A merge would need occurrence-level deduplication and a single consistent
/// ordering across two sources, and getting either subtly wrong produces a console that shows the
/// same error twice or drops one silently. Flushing first costs one small write and makes the two
/// views identical by construction.
/// <para>
/// If the archive is unreachable, reads fall back to the in-memory buffer rather than failing: a
/// degraded Errors tab during a database outage is far more useful than an empty one, and a
/// database outage is exactly when an operator is looking at it.
/// </para>
/// </remarks>
public sealed class DatabaseGatewayErrorStore(
    InMemoryGatewayErrorStore hotStore,
    IGatewayErrorArchiveWriter writer,
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseGatewayErrorStore> logger) : IGatewayErrorStore
{
    public bool IsPersistent => true;

    public void Record(GatewayErrorRecord record) => hotStore.Record(record);

    public Task<GatewayErrorGroupPage> QueryGroupsAsync(
        GatewayErrorQuery query,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            async (archive, services) =>
            {
                var page = await archive.QueryGroupsAsync(query, cancellationToken).ConfigureAwait(false);
                var retention = await ReadRetentionAsync(services, cancellationToken).ConfigureAwait(false);
                return page with
                {
                    DroppedTotal = writer.DroppedTotal,
                    PersistFailedTotal = writer.PersistFailedTotal,
                    PrunedTotal = retention?.PrunedTotal ?? 0,
                    RetainedSinceUtc = retention?.RetainedSinceUtc,
                };
            },
            async () =>
            {
                var page = await hotStore.QueryGroupsAsync(query, cancellationToken).ConfigureAwait(false);
                return page with
                {
                    Degraded = true,
                    DroppedTotal = writer.DroppedTotal,
                    PersistFailedTotal = writer.PersistFailedTotal,
                };
            },
            cancellationToken);

    public Task<GatewayErrorPage> QueryAsync(
        GatewayErrorQuery query,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            archive => archive.QueryAsync(query, cancellationToken),
            async () =>
            {
                var page = await hotStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
                return page with { Degraded = true };
            },
            cancellationToken);

    public Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        ReadAsync(
            archive => archive.GetAsync(id, cancellationToken),
            () => hotStore.GetAsync(id, cancellationToken),
            cancellationToken);

    public Task<GatewayErrorFacets> GetFacetsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default) =>
        ReadAsync(
            archive => archive.GetFacetsAsync(from, to, cancellationToken),
            () => hotStore.GetFacetsAsync(from, to, cancellationToken),
            cancellationToken);

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        // Drop buffered writes before clearing anything, so a record captured a moment ago cannot
        // land in the database after the wipe and reappear as a survivor.
        writer.DiscardPending();
        var removed = await hotStore.ClearAsync(cancellationToken).ConfigureAwait(false);

        using var scope = scopeFactory.CreateScope();
        var archive = scope.ServiceProvider.GetRequiredService<IGatewayErrorArchive>();
        var deleted = await archive.DeleteAllAsync(cancellationToken).ConfigureAwait(false);

        // The wipe rebases the archive, so the retention ledger starts over with it.
        var state = scope.ServiceProvider.GetService<IMaintenanceStateStore>();
        if (state is not null)
        {
            await state.SetAsync(
                MaintenanceStateKeys.ErrorRetention,
                new GatewayErrorRetentionState(),
                cancellationToken).ConfigureAwait(false);
        }

        return Math.Max(removed, deleted);
    }

    private static async Task<GatewayErrorRetentionState?> ReadRetentionAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var state = services.GetService<IMaintenanceStateStore>();
        return state is null
            ? null
            : await state.GetAsync<GatewayErrorRetentionState>(MaintenanceStateKeys.ErrorRetention, cancellationToken)
                .ConfigureAwait(false);
    }

    private Task<T> ReadAsync<T>(
        Func<IGatewayErrorArchive, Task<T>> read,
        Func<Task<T>> fallback,
        CancellationToken cancellationToken) =>
        ReadAsync((archive, _) => read(archive), fallback, cancellationToken);

    private async Task<T> ReadAsync<T>(
        Func<IGatewayErrorArchive, IServiceProvider, Task<T>> read,
        Func<Task<T>> fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.FlushPendingAsync(cancellationToken).ConfigureAwait(false);

            using var scope = scopeFactory.CreateScope();
            var archive = scope.ServiceProvider.GetRequiredService<IGatewayErrorArchive>();
            return await read(archive, scope.ServiceProvider).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Error archive query failed; serving the in-memory buffer instead.");
            return await fallback().ConfigureAwait(false);
        }
    }
}
