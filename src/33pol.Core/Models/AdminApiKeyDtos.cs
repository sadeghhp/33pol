using System.Text.Json.Serialization;
using Pol33.Core.Identity;

namespace Pol33.Core.Models;

public sealed class CreateAdminApiKeyRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApiKeyRole Role { get; init; } = ApiKeyRole.Inference;

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public string? Description { get; init; }

    public string? CostCenter { get; init; }

    /// <summary>
    /// Optional absolute expiry, in UTC. Omit for a key that never expires.
    /// </summary>
    /// <remarks>
    /// The <c>ExpiresAt</c> column, the <c>Expired</c> validation failure and the
    /// <c>expired_api_key</c> error code all existed and were all unreachable: creation hardcoded a
    /// null expiry and no update could set one, so keys were revoke-only and every credential issued
    /// was effectively permanent.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class UpdateAdminApiKeyRequest
{
    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public string? Description { get; init; }

    public string? CostCenter { get; init; }

    /// <summary>
    /// New absolute expiry, in UTC. Only applied when <see cref="UpdateExpiry"/> is set, so an
    /// ordinary metadata edit cannot clear an expiry by omitting the field.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Set to apply <see cref="ExpiresAt"/>; pass it with a null value to remove the expiry.</summary>
    public bool UpdateExpiry { get; init; }
}

public sealed class AdminApiKeyCreatedResponse
{
    public required Guid Id { get; init; }

    public required string Secret { get; init; }

    public required string KeyPrefix { get; init; }

    public required ApiKeyRole Role { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public string? Description { get; init; }

    public string? CostCenter { get; init; }
}

public sealed class ApiKeyUsageSummary
{
    public int RequestCount { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public decimal TotalCost { get; init; }
}

public sealed class AdminApiKeyListItem
{
    public required Guid Id { get; init; }

    public required string KeyPrefix { get; init; }

    public required ApiKeyRole Role { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public string? Description { get; init; }

    public string? CostCenter { get; init; }

    public ApiKeyUsageSummary? UsageSummary { get; init; }

    public bool IsRevoked => RevokedAt is not null;
}

public sealed class AdminApiKeyUsageResponse
{
    public required Guid Id { get; init; }

    public required string KeyPrefix { get; init; }

    public string? Label { get; init; }

    public string? Assignee { get; init; }

    public string? CostCenter { get; init; }

    public required DateOnly FromDate { get; init; }

    public required DateOnly ToDate { get; init; }

    public required ApiKeyUsageSummary Summary { get; init; }

    public required IReadOnlyList<AdminBillingEventListItem> Events { get; init; }
}

public sealed class AdminBillingEventListItem
{
    public required Guid Id { get; init; }

    public required string RequestId { get; init; }

    public Guid? ApiKeyId { get; init; }

    public string? KeyPrefix { get; init; }

    public string? Assignee { get; init; }

    public required string ModelId { get; init; }

    public string? CostCenter { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public decimal? TotalCost { get; init; }

    public double DurationMs { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed class BatchRevokeAdminApiKeysRequest
{
    public IReadOnlyList<Guid> KeyIds { get; init; } = [];
}

public sealed class BatchRevokeAdminApiKeysResponse
{
    public required int RevokedCount { get; init; }
}
