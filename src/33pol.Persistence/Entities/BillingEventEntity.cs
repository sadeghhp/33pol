namespace Pol33.Persistence.Entities;

public sealed class BillingEventEntity
{
    public Guid Id { get; set; }

    public required string RequestId { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? ApiKeyId { get; set; }

    public required string ModelId { get; set; }

    public string? CostCenter { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public decimal? InputCost { get; set; }

    public decimal? OutputCost { get; set; }

    public decimal? TotalCost { get; set; }

    public double DurationMs { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
