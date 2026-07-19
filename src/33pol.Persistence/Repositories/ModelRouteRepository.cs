using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ModelRouteRepository(GatewayDbContext dbContext) : IModelRouteRepository
{
    public async Task<IReadOnlyList<ModelConfig>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.ModelRoutes
            .AsNoTracking()
            .OrderBy(m => m.ModelId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ModelRouteEntityMapper.ToModel).ToList();
    }

    public async Task ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        var now = DateTimeOffset.UtcNow;

        // Replace the route table wholesale. RemoveRange keeps this provider-agnostic (the EF InMemory
        // provider used by tests does not support ExecuteDelete).
        var existing = await dbContext.ModelRoutes
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.ModelRoutes.RemoveRange(existing);

        foreach (var model in models)
        {
            dbContext.ModelRoutes.Add(ModelRouteEntityMapper.ToEntity(model, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
