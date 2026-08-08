using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class DailyUsageRollupRepository(GatewayDbContext dbContext) : IDailyUsageRollupRepository
{
    public async Task<IReadOnlyList<DailyUsageRollupRecord>> GetRollupsAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.DailyUsageRollups.AsNoTracking();

        if (fromDate is not null)
        {
            query = query.Where(r => r.UsageDate >= fromDate.Value);
        }

        if (toDate is not null)
        {
            query = query.Where(r => r.UsageDate <= toDate.Value);
        }

        if (tenantId is not null)
        {
            query = query.Where(r => r.TenantId == tenantId.Value);
        }

        var entities = await query
            .OrderBy(r => r.UsageDate)
            .ThenBy(r => r.TenantId)
            .ThenBy(r => r.ModelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(DailyUsageRollupEntityMapper.ToRecord).ToList();
    }

    public async Task UpsertRollupsAsync(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        if (rollups.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var dates = rollups.Select(r => r.UsageDate).Distinct().ToList();
        var tenantIds = rollups.Select(r => r.TenantId).Distinct().ToList();

        var existing = await dbContext.DailyUsageRollups
            .Where(r => dates.Contains(r.UsageDate) && tenantIds.Contains(r.TenantId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingByKey = existing.ToDictionary(e =>
            DailyUsageRollupKey.FromRecord(DailyUsageRollupEntityMapper.ToRecord(e)));

        foreach (var rollup in rollups)
        {
            var key = DailyUsageRollupKey.FromRecord(rollup);
            if (existingByKey.TryGetValue(key, out var entity))
            {
                DailyUsageRollupEntityMapper.ApplyRecord(entity, rollup, now);
            }
            else
            {
                dbContext.DailyUsageRollups.Add(DailyUsageRollupEntityMapper.ToEntity(rollup, now));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies additive deltas atomically with respect to other writers.
    /// </summary>
    /// <remarks>
    /// The read and the write happen inside one <c>BEGIN IMMEDIATE</c> transaction, which takes
    /// SQLite's write lock up front so a second writer blocks before it can read a stale starting
    /// value — the failure mode that silently dropped one writer's tokens and cost when two
    /// overlapped.
    ///
    /// <para>The transaction is started against the raw <see cref="SqliteConnection"/> with
    /// <c>deferred: false</c>, deliberately. EF's
    /// <c>BeginTransactionAsync(IsolationLevel.Serializable)</c> goes through the overload that
    /// leaves the transaction <em>deferred</em>: the read takes only a shared lock and the write has
    /// to upgrade it, which under WAL fails with <c>SQLITE_BUSY_SNAPSHOT</c> rather than waiting —
    /// and <c>busy_timeout</c> does not retry that, because the snapshot is genuinely stale. The
    /// resulting exception surfaced as a whole batch of rollups silently going missing.</para>
    ///
    /// The EF InMemory provider used by unit tests supports neither transactions nor row locking, so
    /// there the deltas are applied directly; the concurrency guarantee is covered by the SQLite
    /// integration tests.
    /// </remarks>
    public async Task IncrementRollupsAsync(
        IReadOnlyList<DailyUsageRollupDelta> deltas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0)
        {
            return;
        }

        if (!dbContext.Database.IsRelational())
        {
            await ApplyDeltasAsync(deltas, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (dbContext.Database.GetDbConnection() is not SqliteConnection sqliteConnection)
        {
            // Non-SQLite relational provider: its own Serializable maps to real serializable
            // isolation, so the standard path is correct there.
            await using var providerTransaction = await dbContext.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            await ApplyDeltasAsync(deltas, cancellationToken).ConfigureAwait(false);
            await providerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (sqliteConnection.State != System.Data.ConnectionState.Open)
        {
            await sqliteConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var immediateTransaction = sqliteConnection.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: false);

        await using (immediateTransaction.ConfigureAwait(false))
        {
            await dbContext.Database.UseTransactionAsync(immediateTransaction, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await ApplyDeltasAsync(deltas, cancellationToken).ConfigureAwait(false);
                await immediateTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await dbContext.Database.UseTransactionAsync(null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyDeltasAsync(
        IReadOnlyList<DailyUsageRollupDelta> deltas,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dates = deltas.Select(d => d.UsageDate).Distinct().ToList();
        var tenantIds = deltas.Select(d => d.TenantId).Distinct().ToList();

        // One query for every bucket this batch touches, rather than one per delta.
        var existing = await dbContext.DailyUsageRollups
            .Where(r => dates.Contains(r.UsageDate) && tenantIds.Contains(r.TenantId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingByKey = existing.ToDictionary(e =>
            DailyUsageRollupKey.FromRecord(DailyUsageRollupEntityMapper.ToRecord(e)));

        foreach (var delta in deltas)
        {
            var key = new DailyUsageRollupKey(
                delta.UsageDate,
                delta.TenantId,
                delta.ModelId,
                DailyUsageRollupKey.NormalizeCostCenter(delta.CostCenter));
            if (existingByKey.TryGetValue(key, out var entity))
            {
                entity.PromptTokens += delta.PromptTokens;
                entity.CompletionTokens += delta.CompletionTokens;
                entity.TotalCost += delta.TotalCost;
                entity.RequestCount += delta.RequestCount;
                entity.UpdatedAt = now;
                continue;
            }

            var created = DailyUsageRollupEntityMapper.ToEntity(
                new DailyUsageRollupRecord(
                    delta.UsageDate,
                    delta.TenantId,
                    delta.ModelId,
                    delta.CostCenter,
                    delta.PromptTokens,
                    delta.CompletionTokens,
                    delta.TotalCost,
                    delta.RequestCount),
                now);

            dbContext.DailyUsageRollups.Add(created);

            // Later deltas in the same batch for this bucket must accumulate onto the row just
            // created, not insert a duplicate that would violate the unique index.
            existingByKey[key] = created;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
