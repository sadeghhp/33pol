using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface IApiKeyRepository
{
    Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> UpdateMetadataAsync(
        Guid id,
        ApiKeyMetadataUpdate update,
        CancellationToken cancellationToken = default);

    Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default);
}
