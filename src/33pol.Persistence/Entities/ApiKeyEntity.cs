using Pol33.Core.Identity;

namespace Pol33.Persistence.Entities;

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public TenantEntity Tenant { get; set; } = null!;

    public string KeyHash { get; set; } = string.Empty;

    public string KeyPrefix { get; set; } = string.Empty;

    public ApiKeyRole Role { get; set; }

    public List<string> Scopes { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
