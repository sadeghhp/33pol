namespace Pol33.Persistence.Entities;

public sealed class PlanEntity
{
    public Guid Id { get; set; }

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string? RateCardSlug { get; set; }

    public long? MonthlyTokenLimit { get; set; }

    public int? RequestsPerMinute { get; set; }

    public int? ConcurrencyLimit { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
