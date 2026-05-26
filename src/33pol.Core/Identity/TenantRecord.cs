namespace Pol33.Core.Identity;

public sealed class TenantRecord
{
    public required Guid Id { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? PlanSlug { get; init; }

    public string? CostCenter { get; init; }

    public required bool IsActive { get; init; }
}
