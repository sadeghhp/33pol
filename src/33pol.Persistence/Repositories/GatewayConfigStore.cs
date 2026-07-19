using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class GatewayConfigStore(GatewayDbContext dbContext) : IGatewayConfigStore
{
    // The config version is a singleton row; the fixed key keeps reads and bumps a pure upsert.
    private const int ConfigVersionRowId = 1;

    public async Task<GatewayConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);

        return new GatewayConfigSnapshot
        {
            Version = version,
        };
    }

    public async Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        return row?.Version ?? 0;
    }

    public async Task<long> IncrementVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ConfigVersionEntity { Id = ConfigVersionRowId, Version = 0 };
            dbContext.ConfigVersions.Add(row);
        }

        row.Version += 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Version;
    }
}
