using Pol33.Core.Identity;

namespace Pol33.Persistence.Entities;

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public TenantEntity Tenant { get; set; } = null!;

    public required string KeyHash { get; set; }

    public required string KeyPrefix { get; set; }

    public ApiKeyRole Role { get; set; }

    public List<string> Scopes { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Set when an operator files the (already revoked) key away. Archived keys keep every usage
    /// record they earned; they are only hidden from the working set.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public string? Label { get; set; }

    public string? Assignee { get; set; }

    public string? Description { get; set; }

    public string? CostCenter { get; set; }
}
