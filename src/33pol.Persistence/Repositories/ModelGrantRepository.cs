using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ModelGrantRepository : IModelGrantRepository
{
    private readonly GatewayDbContext _db;

    public ModelGrantRepository(GatewayDbContext db) => _db = db;

    public async Task<IReadOnlyList<ModelGrantRecord>> ListByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _db.ModelGrants
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.ModelPattern)
            .ToListAsync(cancellationToken);

        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task<ModelGrantRecord> AddAsync(ModelGrantRecord grant, CancellationToken cancellationToken = default)
    {
        var entity = IdentityEntityMapper.ToEntity(grant);
        _db.ModelGrants.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return IdentityEntityMapper.ToRecord(entity);
    }
}
