namespace Pol33.Persistence.Entities;

public sealed class DailyUsageRollupEntity
{
    public Guid Id { get; set; }

    public DateOnly UsageDate { get; set; }

    /// <summary><see cref="Guid.Empty"/> for anonymous traffic (never NULL; see the entity configuration).</summary>
    public Guid TenantId { get; set; }

    public required string ModelId { get; set; }

    /// <summary>Empty string for "no cost centre" (never NULL; see the entity configuration).</summary>
    public required string CostCenter { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public decimal TotalCost { get; set; }

    public int RequestCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
