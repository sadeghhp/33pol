using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface IApiKeyRepository
{
    Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Loads many keys at once; ids that do not exist are simply absent from the result.</summary>
    Task<IReadOnlyList<ApiKeyRecord>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads every key stored under any of <paramref name="keyPrefixes"/>. Prefixes are not unique, so the
    /// caller must verify the hash of each candidate before treating one as authenticated.
    /// </summary>
    Task<IReadOnlyList<ApiKeyRecord>> FindByPrefixesAsync(
        IReadOnlyCollection<string> keyPrefixes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> UpdateMetadataAsync(
        Guid id,
        ApiKeyMetadataUpdate update,
        CancellationToken cancellationToken = default);

    Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    /// <summary>Active (not revoked, not yet expired) keys whose expiry falls on or before <paramref name="before"/>.</summary>
    Task<IReadOnlyList<ApiKeyRecord>> ListExpiringAsync(DateTimeOffset before, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKeyRecord>>([]);

    /// <summary>Active keys not used since <paramref name="idleSince"/> (never-used keys count from their creation).</summary>
    Task<IReadOnlyList<ApiKeyRecord>> ListIdleAsync(DateTimeOffset idleSince, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKeyRecord>>([]);

    /// <summary>Total and revoked key counts across every tenant.</summary>
    Task<(int Total, int Revoked)> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((0, 0));
}
