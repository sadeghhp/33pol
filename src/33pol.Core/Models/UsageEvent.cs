namespace Pol33.Core.Models;

public sealed class UsageEvent
{
    public required string RequestId { get; init; }

    public string? TenantId { get; init; }

    public string? ApiKeyId { get; init; }

    public required string ModelId { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public double DurationMs { get; init; }

    public string? CostCenter { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }
}
