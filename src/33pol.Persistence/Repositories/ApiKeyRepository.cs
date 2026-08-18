using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ApiKeyRepository : IApiKeyRepository
{
    private readonly GatewayDbContext _db;

    public ApiKeyRepository(GatewayDbContext db) => _db = db;

    public async Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

        return entity is null ? null : IdentityEntityMapper.ToRecord(entity);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        // Chunked to stay well under SQLite's default 999 bound-parameter limit.
        var result = new List<ApiKeyRecord>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            var entities = await _db.ApiKeys
                .AsNoTracking()
                .Where(k => chunk.Contains(k.Id))
                .ToListAsync(cancellationToken);
            result.AddRange(entities.Select(IdentityEntityMapper.ToRecord));
        }

        return result;
    }

    public async Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        var entity = await _db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyPrefix == keyPrefix, cancellationToken);

        return entity is null ? null : IdentityEntityMapper.ToRecord(entity);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> FindByPrefixesAsync(
        IReadOnlyCollection<string> keyPrefixes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyPrefixes);

        if (keyPrefixes.Count == 0)
        {
            return [];
        }

        var prefixes = keyPrefixes.ToArray();
        var entities = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => prefixes.Contains(k.KeyPrefix))
            .ToListAsync(cancellationToken);

        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default)
    {
        var entity = IdentityEntityMapper.ToEntity(apiKey);
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return IdentityEntityMapper.ToRecord(entity);
    }

    public async Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"API key '{id}' was not found.");

        entity.RevokedAt = revokedAt;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApiKeyRecord> UpdateMetadataAsync(
        Guid id,
        ApiKeyMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"API key '{id}' was not found.");

        entity.Label = NormalizeOptional(update.Label);
        entity.Assignee = NormalizeOptional(update.Assignee);
        entity.Description = NormalizeOptional(update.Description);
        entity.CostCenter = NormalizeOptional(update.CostCenter);

        if (update.UpdateExpiry)
        {
            entity.ExpiresAt = update.ExpiresAt;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return IdentityEntityMapper.ToRecord(entity);
    }

    public async Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.LastUsedAt = atUtc;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
