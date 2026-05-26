using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class TenantRepository : ITenantRepository
{
    private readonly GatewayDbContext _db;

    public TenantRepository(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<TenantRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        return entity?.ToRecord();
    }

    public async Task<TenantRecord?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        return entity?.ToRecord();
    }

    public async Task<TenantRecord> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Slug = request.Slug,
            Name = request.Name,
            PlanSlug = request.PlanSlug,
            CostCenter = request.CostCenter,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Tenants.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.ToRecord();
    }
}
