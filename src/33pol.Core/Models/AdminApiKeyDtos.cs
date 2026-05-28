using System.Text.Json.Serialization;
using Pol33.Core.Identity;

namespace Pol33.Core.Models;

public sealed class CreateAdminApiKeyRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApiKeyRole Role { get; init; } = ApiKeyRole.Inference;

    public IReadOnlyList<string> Scopes { get; init; } = [];
}

public sealed class AdminApiKeyCreatedResponse
{
    public required Guid Id { get; init; }

    public required string Secret { get; init; }

    public required string KeyPrefix { get; init; }

    public required ApiKeyRole Role { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class AdminApiKeyListItem
{
    public required Guid Id { get; init; }

    public required string KeyPrefix { get; init; }

    public required ApiKeyRole Role { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsRevoked => RevokedAt is not null;
}

public sealed class BatchRevokeAdminApiKeysRequest
{
    public IReadOnlyList<Guid> KeyIds { get; init; } = [];
}

public sealed class BatchRevokeAdminApiKeysResponse
{
    public required int RevokedCount { get; init; }
}
