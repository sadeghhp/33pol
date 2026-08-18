using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Persistence.Infrastructure;
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
            var storedTenantId = DailyUsageRollupEntityMapper.ToStoredTenantId(tenantId);
            query = query.Where(r => r.TenantId == storedTenantId);
        }

        var entities = await query
            .OrderBy(r => r.UsageDate)
            .ThenBy(r => r.TenantId)
            .ThenBy(r => r.ModelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(DailyUsageRollupEntityMapper.ToRecord).ToList();
    }

    public async Task<IReadOnlyList<DailyUsageRollupRecord>> GetScopedRollupsAsync(
        UsageScope scope,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = dbContext.DailyUsageRollups.AsNoTracking();

        if (fromDate is not null)
        {
            query = query.Where(r => r.UsageDate >= fromDate.Value);
        }

        if (toDate is not null)
        {
            query = query.Where(r => r.UsageDate <= toDate.Value);
        }

        // Anonymous buckets are stored under the Guid.Empty sentinel (see the entity configuration),
        // not NULL, so the unique index covers them.
        var anonymous = DailyUsageRollupEntityMapper.AnonymousTenantId;
        if (scope.TenantId is Guid tenantId)
        {
            query = scope.IncludeAnonymous
                ? query.Where(r => r.TenantId == tenantId || r.TenantId == anonymous)
                : query.Where(r => r.TenantId == tenantId);
        }
        else if (!scope.IncludeAnonymous)
        {
            query = query.Where(r => r.TenantId != anonymous);
        }

        var entities = await query
            .OrderBy(r => r.UsageDate)
            .ThenBy(r => r.TenantId)
            .ThenBy(r => r.ModelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(DailyUsageRollupEntityMapper.ToRecord).ToList();
    }

    /// <summary>
    /// Replaces the stored totals for each bucket (reconciliation). Runs under the same
    /// <c>BEGIN IMMEDIATE</c> helper as <see cref="IncrementRollupsAsync"/>, so an overlapping
    /// increment cannot slip between the read and the insert and create a second row for a bucket.
    /// </summary>
    public async Task UpsertRollupsAsync(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        if (rollups.Count == 0)
        {
            return;
        }

        await GatewayWriteTransaction.RunAsync(dbContext, ct => ApplyUpsertsAsync(rollups, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyUpsertsAsync(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dates = rollups.Select(r => r.UsageDate).Distinct().ToList();
        var tenantIds = rollups.Select(r => DailyUsageRollupEntityMapper.ToStoredTenantId(r.TenantId)).Distinct().ToList();

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
                var created = DailyUsageRollupEntityMapper.ToEntity(rollup, now);
                dbContext.DailyUsageRollups.Add(created);
                existingByKey[key] = created;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies additive deltas atomically with respect to other writers.
    /// </summary>
    /// <remarks>
    /// The read and the write happen inside one <c>BEGIN IMMEDIATE</c> transaction (see
    /// <see cref="GatewayWriteTransaction"/>), which takes SQLite's write lock up front so a second
    /// writer blocks before it can read a stale starting value — the failure mode that silently
    /// dropped one writer's tokens and cost when two overlapped. The EF InMemory provider used by
    /// unit tests supports neither transactions nor row locking, so there the deltas are applied
    /// directly; the concurrency guarantee is covered by the SQLite integration tests.
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

        await GatewayWriteTransaction.RunAsync(dbContext, ct => ApplyDeltasAsync(deltas, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyDeltasAsync(
        IReadOnlyList<DailyUsageRollupDelta> deltas,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dates = deltas.Select(d => d.UsageDate).Distinct().ToList();
        var tenantIds = deltas.Select(d => DailyUsageRollupEntityMapper.ToStoredTenantId(d.TenantId)).Distinct().ToList();

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
