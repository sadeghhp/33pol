using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Infrastructure;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ModelRouteRepository(GatewayDbContext dbContext) : IModelRouteRepository
{
    /// <summary>
    /// Routes carry their own version row, separate from the general config version (row 1), so a
    /// CORS or rate-limit change does not make an unrelated route write look like a conflict.
    /// </summary>
    private const int RouteVersionRowId = 2;

    public async Task<IReadOnlyList<ModelConfig>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.ModelRoutes
            .AsNoTracking()
            .OrderBy(m => m.ModelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ModelRouteEntityMapper.ToModel).ToList();
    }

    public async Task<ModelRouteSnapshot> ListWithVersionAsync(CancellationToken cancellationToken = default)
    {
        var models = await ListAsync(cancellationToken).ConfigureAwait(false);
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);
        return new ModelRouteSnapshot(models, version);
    }

    public async Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == RouteVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        return row?.Version ?? 0;
    }

    public async Task<long> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        // The whole read-check-write runs in one BEGIN IMMEDIATE transaction on SQLite (see
        // GatewayWriteTransaction), so a second writer blocks on the write lock before it reads the
        // version and then sees the bumped value — surfacing as ModelRouteVersionConflictException,
        // not SQLITE_BUSY_SNAPSHOT. The EF InMemory provider (tests) has no transactions; there the
        // check still runs, just without isolation.
        long newVersion = 0;
        await GatewayWriteTransaction.RunAsync(
            dbContext,
            async ct =>
            {
                var versionRow = await dbContext.ConfigVersions
                    .FirstOrDefaultAsync(c => c.Id == RouteVersionRowId, ct)
                    .ConfigureAwait(false);

                var currentVersion = versionRow?.Version ?? 0;
                if (expectedVersion is long expected && expected != currentVersion)
                {
                    throw new ModelRouteVersionConflictException(expected, currentVersion);
                }

                var now = DateTimeOffset.UtcNow;

                // Replace the route table wholesale. RemoveRange keeps this provider-agnostic (the EF
                // InMemory provider used by tests does not support ExecuteDelete).
                var existing = await dbContext.ModelRoutes
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                dbContext.ModelRoutes.RemoveRange(existing);

                foreach (var model in models)
                {
                    dbContext.ModelRoutes.Add(ModelRouteEntityMapper.ToEntity(model, now));
                }

                if (versionRow is null)
                {
                    versionRow = new ConfigVersionEntity { Id = RouteVersionRowId, Version = currentVersion };
                    dbContext.ConfigVersions.Add(versionRow);
                }

                versionRow.Version = currentVersion + 1;
                versionRow.UpdatedAt = now;

                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                newVersion = versionRow.Version;
            },
            cancellationToken).ConfigureAwait(false);

        return newVersion;
    }
}
