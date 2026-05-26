namespace Pol33.Core.Identity;

public sealed record TenantRecord(
    Guid Id,
    string Slug,
    string Name,
    string? PlanSlug,
    string? CostCenter,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
