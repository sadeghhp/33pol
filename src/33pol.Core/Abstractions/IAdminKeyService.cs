using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IAdminKeyService
{
    Task<AdminApiKeyCreatedResponse> CreateAsync(
        Guid tenantId,
        CreateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The tenant's keys. Archived keys are excluded unless <paramref name="includeArchived"/> is set.
    /// <paramref name="actorKeyId"/> is the calling admin's own key, excluded from
    /// <see cref="AdminApiKeyListItem.CanDelete"/> so the console never offers self-destruction.
    /// </summary>
    Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        bool includeUsageSummary = false,
        bool includeArchived = false,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default);

    Task<AdminApiKeyListItem> UpdateAsync(
        Guid tenantId,
        Guid keyId,
        UpdateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminApiKeyUsageResponse> GetUsageAsync(
        Guid tenantId,
        Guid keyId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the key. Idempotent: revoking an already-revoked key succeeds silently, so a client
    /// retrying after a timeout during an incident is not told its first attempt failed.
    /// </summary>
    Task RevokeAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default);

    Task<int> RevokeManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> keyIds,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Files an already-revoked key away, preserving every usage record it earned. Throws
    /// <see cref="Identity.ApiKeyLifecycleException"/> when the key is still live or already archived.
    /// </summary>
    Task ArchiveAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns an archived key to the working set. It stays revoked.</summary>
    Task UnarchiveAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a key that has never been used. Throws
    /// <see cref="Identity.ApiKeyLifecycleException"/> with code <c>key_has_usage</c> when the key has
    /// any recorded usage — those keys are archived, never deleted.
    /// </summary>
    /// <returns>A snapshot of the key as it was, for the audit entry — after the row is gone its id
    /// resolves to nothing, so the id alone would record which key was destroyed only in name.</returns>
    Task<AdminApiKeyListItem> DeleteAsync(
        Guid tenantId,
        Guid keyId,
        Guid? actorKeyId,
        string? confirmKeyPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The key's recorded lifecycle history. Resolves for permanently deleted keys too — that is the
    /// point of keeping the history in a table of its own.
    /// </summary>
    Task<AdminApiKeyLifecycleResponse> GetLifecycleAsync(
        Guid tenantId,
        Guid keyId,
        CancellationToken cancellationToken = default);
}
