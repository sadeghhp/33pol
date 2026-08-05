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
    /// The read and the write happen inside one serializable transaction. On SQLite that maps to
    /// <c>BEGIN IMMEDIATE</c>, which takes the write lock up front, so a second writer blocks before
    /// it can read a stale starting value — the failure mode that silently dropped one writer's
    /// tokens and cost when two overlapped.
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

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        await ApplyDeltasAsync(deltas, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
