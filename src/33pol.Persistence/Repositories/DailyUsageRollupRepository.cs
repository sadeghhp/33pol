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
}
