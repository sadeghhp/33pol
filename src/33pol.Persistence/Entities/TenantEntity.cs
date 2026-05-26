namespace Pol33.Persistence.Entities;

public sealed class TenantEntity
{
    public Guid Id { get; set; }

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string? PlanSlug { get; set; }

    public string? CostCenter { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ApiKeyEntity> ApiKeys { get; set; } = [];

    public ICollection<ModelGrantEntity> ModelGrants { get; set; } = [];
}
