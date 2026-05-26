using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface IApiKeyRepository
{
    Task<ApiKeyRecord?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKeyRecord>> ListByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class CreateApiKeyRequest
{
    public required Guid TenantId { get; init; }

    public required string KeyHash { get; init; }

    public required string KeyPrefix { get; init; }

    public required ApiKeyRole Role { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    public DateTimeOffset? ExpiresAt { get; init; }
}
