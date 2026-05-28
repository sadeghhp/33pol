using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IAdminKeyService
{
    Task<AdminApiKeyCreatedResponse> CreateAsync(
        Guid tenantId,
        CreateAdminApiKeyRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminApiKeyListItem>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken = default);

    Task<int> RevokeManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> keyIds,
        CancellationToken cancellationToken = default);
}
