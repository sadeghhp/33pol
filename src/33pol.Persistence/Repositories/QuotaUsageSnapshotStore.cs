using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class QuotaUsageSnapshotStore(GatewayDbContext dbContext) : IQuotaUsageSnapshotStore
{
    public async Task<IReadOnlyList<QuotaUsageSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.QuotaUsageSnapshots
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities
            .Select(e => new QuotaUsageSnapshot(e.PartitionKey, e.Period, e.Used))
            .ToList();
    }

    public async Task SaveAsync(IReadOnlyList<QuotaUsageSnapshot> usages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usages);
        if (usages.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var keys = usages.Select(u => u.PartitionKey).ToList();

        var existing = await dbContext.QuotaUsageSnapshots
            .Where(q => keys.Contains(q.PartitionKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(e => e.PartitionKey, StringComparer.Ordinal);

        foreach (var usage in usages)
        {
            if (existingByKey.TryGetValue(usage.PartitionKey, out var entity))
            {
                entity.Period = usage.Period;
                entity.Used = usage.Used;
                entity.UpdatedAt = now;
            }
            else
            {
                dbContext.QuotaUsageSnapshots.Add(new QuotaUsageSnapshotEntity
                {
                    PartitionKey = usage.PartitionKey,
                    Period = usage.Period,
                    Used = usage.Used,
                    UpdatedAt = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
