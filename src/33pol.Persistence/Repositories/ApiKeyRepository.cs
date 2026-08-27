using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
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
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId);

        if (!includeArchived)
        {
            query = query.Where(k => k.ArchivedAt == null);
        }

        var entities = await query
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

    public async Task RestoreRevokedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"API key '{id}' was not found.");

        entity.RevokedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"API key '{id}' was not found.");

        entity.ArchivedAt = archivedAt;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnarchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"API key '{id}' was not found.");

        // RevokedAt is deliberately untouched: unarchiving files a key back into the working set,
        // it does not resurrect the credential.
        entity.ArchivedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        // Remove rather than ExecuteDelete: the EF InMemory provider used across the test suite does
        // not implement bulk delete. Model grants go with it through the configured FK cascade; on
        // the InMemory provider, which enforces no FKs, they are removed explicitly first so both
        // providers leave the same state behind.
        var grants = await _db.ApiKeyModelGrants
            .Where(g => g.ApiKeyId == id)
            .ToListAsync(cancellationToken);
        if (grants.Count > 0)
        {
            _db.ApiKeyModelGrants.RemoveRange(grants);
        }

        // Rate-limit rules name their subject in a free-text TargetKey with no foreign key, so nothing
        // in the schema clears them when the subject goes. Left behind they are permanent clutter in
        // the rules admin surface, pointing at an id that resolves to nothing. Both key-bearing scopes
        // are covered: "api_key" targets the id alone, "api_key_model" the "id|model" pair.
        // Lowered on both sides because TargetKey is stored exactly as an admin typed it while the
        // scopes that carry a key id match it case-insensitively.
        var keyId = id.ToString().ToLowerInvariant();
        var targetPrefix = keyId + "|";
        var orphanedRules = await _db.RateLimitRules
            .Where(r => (r.Scope == RateLimitScopeNames.ApiKey && r.TargetKey.ToLower() == keyId) ||
                        (r.Scope == RateLimitScopeNames.ApiKeyModel && r.TargetKey.ToLower().StartsWith(targetPrefix)))
            .ToListAsync(cancellationToken);
        if (orphanedRules.Count > 0)
        {
            _db.RateLimitRules.RemoveRange(orphanedRules);
        }

        _db.ApiKeys.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> CountActiveAdminKeysAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.ApiKeys
            .AsNoTracking()
            .CountAsync(
                k => k.TenantId == tenantId &&
                     k.RevokedAt == null &&
                     k.ArchivedAt == null &&
                     (k.ExpiresAt == null || k.ExpiresAt > now) &&
                     (k.Role == ApiKeyRole.Admin || k.Role == ApiKeyRole.Both),
                cancellationToken);
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

    public async Task<IReadOnlyList<ApiKeyRecord>> ListExpiringAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entities = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.RevokedAt == null && k.ArchivedAt == null &&
                        k.ExpiresAt != null && k.ExpiresAt <= before && k.ExpiresAt > now)
            .OrderBy(k => k.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListIdleAsync(DateTimeOffset idleSince, CancellationToken cancellationToken = default)
    {
        var entities = await _db.ApiKeys
            .AsNoTracking()
            .Where(k => k.RevokedAt == null && k.ArchivedAt == null && (k.LastUsedAt ?? k.CreatedAt) <= idleSince)
            .OrderBy(k => k.LastUsedAt ?? k.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task<(int Total, int Revoked, int Archived)> CountAsync(CancellationToken cancellationToken = default)
    {
        // Total counts the working set only. Rolling archived keys into it would make the Overview
        // headline creep upward as archiving is adopted, which is the opposite of what archiving is for.
        var total = await _db.ApiKeys.CountAsync(k => k.ArchivedAt == null, cancellationToken);
        var revoked = await _db.ApiKeys.CountAsync(
            k => k.RevokedAt != null && k.ArchivedAt == null,
            cancellationToken);
        var archived = await _db.ApiKeys.CountAsync(k => k.ArchivedAt != null, cancellationToken);
        return (total, revoked, archived);
    }
}
