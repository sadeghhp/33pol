namespace Pol33.Persistence.Entities;

public sealed class QuotaUsageEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public long UsedTokens { get; set; }

    public long UsedRequests { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
