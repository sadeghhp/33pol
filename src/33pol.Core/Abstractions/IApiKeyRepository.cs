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

    /// <summary>
    /// Every key the tenant owns, newest first. Archived keys are excluded unless
    /// <paramref name="includeArchived"/> is set, so the operational surfaces that call this get the
    /// working set by default rather than every credential the tenant has ever held.
    /// </summary>
    Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(
        Guid tenantId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>Files an already-revoked key away. Nothing else about the key changes.</summary>
    Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Clears the archive stamp. The key stays revoked — archiving is not a way back to active.</summary>
    Task UnarchiveAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Permanently removes the key row (and, by cascade, its model grants). Returns false when the id
    /// no longer exists. Callers must have established that the key has no usage history first —
    /// this method enforces nothing.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Keys for the tenant that can still authenticate as an admin: role Admin or Both, not revoked,
    /// not archived, and not past their expiry. Backs the guard against revoking a tenant's last way in.
    /// </summary>
    Task<int> CountActiveAdminKeysAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

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

    /// <summary>
    /// Key counts across every tenant. <c>Total</c> excludes archived keys so the Overview headline
    /// means "keys that exist operationally"; archived keys are reported separately.
    /// </summary>
    Task<(int Total, int Revoked, int Archived)> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((0, 0, 0));
}
