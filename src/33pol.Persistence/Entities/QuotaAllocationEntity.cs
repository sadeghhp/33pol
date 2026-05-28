namespace Pol33.Persistence.Entities;

public sealed class QuotaAllocationEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public long TokenLimit { get; set; }

    public long RequestLimit { get; set; }

    public decimal SoftLimitRatio { get; set; } = 0.9m;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
