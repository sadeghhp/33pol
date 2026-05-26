namespace Pol33.Persistence.Entities;

public sealed class DailyUsageRollupEntity
{
    public Guid Id { get; set; }

    public DateOnly UsageDate { get; set; }

    public Guid? TenantId { get; set; }

    public required string ModelId { get; set; }

    public string? CostCenter { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public decimal TotalCost { get; set; }

    public int RequestCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
