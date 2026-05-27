using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class ApiKeyModelGrantRepository : IApiKeyModelGrantRepository
{
    private readonly GatewayDbContext _db;

    public ApiKeyModelGrantRepository(GatewayDbContext db) => _db = db;

    public async Task<IReadOnlyList<ApiKeyModelGrantRecord>> ListByApiKeyAsync(
        Guid apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _db.ApiKeyModelGrants
            .AsNoTracking()
            .Where(g => g.ApiKeyId == apiKeyId)
            .OrderBy(g => g.ModelPattern)
            .ToListAsync(cancellationToken);

        return entities.Select(IdentityEntityMapper.ToRecord).ToList();
    }

    public async Task ReplaceForApiKeyAsync(
        Guid apiKeyId,
        IReadOnlyList<string> modelPatterns,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.ApiKeyModelGrants
            .Where(g => g.ApiKeyId == apiKeyId)
            .ToListAsync(cancellationToken);

        _db.ApiKeyModelGrants.RemoveRange(existing);

        foreach (var pattern in NormalizePatterns(modelPatterns))
        {
            _db.ApiKeyModelGrants.Add(new Entities.ApiKeyModelGrantEntity
            {
                Id = Guid.NewGuid(),
                ApiKeyId = apiKeyId,
                ModelPattern = pattern,
                Effect = GrantEffect.Allow,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static IEnumerable<string> NormalizePatterns(IReadOnlyList<string> modelPatterns) =>
        modelPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
