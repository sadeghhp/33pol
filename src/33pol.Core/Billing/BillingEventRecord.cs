namespace Pol33.Core.Billing;

public sealed record BillingEventRecord(
    Guid Id,
    string RequestId,
    Guid? TenantId,
    Guid? ApiKeyId,
    string ModelId,
    string? CostCenter,
    long PromptTokens,
    long CompletionTokens,
    decimal? InputCost,
    decimal? OutputCost,
    decimal? TotalCost,
    double DurationMs,
    DateTimeOffset RecordedAt);
