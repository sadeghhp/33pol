namespace Pol33.Core.Identity;

public sealed class ApiKeyRecord
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string TenantSlug { get; init; }

    public required ApiKeyRole Role { get; init; }

    public required string KeyHash { get; init; }

    public required string KeyPrefix { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsActive =>
        RevokedAt is null &&
        (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}
