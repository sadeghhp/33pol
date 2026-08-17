namespace Pol33.Persistence.Entities;

/// <summary>
/// One persisted row of the dashboard's recent-requests feed. The whole feed (capped at ~500 rows)
/// is rewritten on each snapshot flush so it survives container recreation.
/// </summary>
public sealed class RecentRequestSnapshotEntity
{
    public long Id { get; set; }

    public required string RequestId { get; set; }

    public required string Method { get; set; }

    public required string Path { get; set; }

    public string? ModelId { get; set; }

    public string? TenantId { get; set; }

    public int StatusCode { get; set; }

    public double DurationMs { get; set; }

    public bool IsStreaming { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public string? CostCenter { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public long? TotalTokens { get; set; }

    public string? TokenSource { get; set; }

    public decimal? InputCost { get; set; }

    public decimal? OutputCost { get; set; }

    public decimal? TotalCost { get; set; }

    public string? Currency { get; set; }

    public string? PricingStatus { get; set; }
}
