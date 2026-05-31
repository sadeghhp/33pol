namespace Pol33.Core.Identity;

public sealed record ApiKeyRecord(
    Guid Id,
    Guid TenantId,
    string KeyHash,
    string KeyPrefix,
    ApiKeyRole Role,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    string? Label = null,
    string? Assignee = null,
    string? Description = null,
    string? CostCenter = null);
