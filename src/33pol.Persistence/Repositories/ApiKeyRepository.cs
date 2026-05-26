using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ApiKeyRepository : IApiKeyRepository
{
    private readonly GatewayDbContext _db;

    public ApiKeyRepository(GatewayDbContext db)
    {
        _db = db;
    }

    public async Task<ApiKeyRecord?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.AsNoTracking()
            .Include(k => k.Tenant)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash, cancellationToken);
        return entity?.ToRecord();
    }

    public async Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.AsNoTracking()
            .Include(k => k.Tenant)
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        return entity?.ToRecord();
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListByTenantIdAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _db.ApiKeys.AsNoTracking()
            .Include(k => k.Tenant)
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.KeyPrefix)
            .ToListAsync(cancellationToken);
        return entities.Select(e => e.ToRecord()).ToList();
    }

    public async Task<ApiKeyRecord> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        var tenantExists = await _db.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken);
        if (!tenantExists)
        {
            throw new InvalidOperationException($"Tenant '{request.TenantId}' was not found.");
        }

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            KeyHash = request.KeyHash,
            KeyPrefix = request.KeyPrefix,
            Role = request.Role,
            Scopes = request.Scopes.ToList(),
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return (await _db.ApiKeys.AsNoTracking()
            .Include(k => k.Tenant)
            .FirstAsync(k => k.Id == entity.Id, cancellationToken))
            .ToRecord();
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (entity is null || entity.RevokedAt is not null)
        {
            return false;
        }

        entity.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
