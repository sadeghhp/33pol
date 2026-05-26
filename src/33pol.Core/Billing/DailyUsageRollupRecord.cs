namespace Pol33.Core.Billing;

public sealed record DailyUsageRollupRecord(
    DateOnly UsageDate,
    Guid? TenantId,
    string ModelId,
    string? CostCenter,
    long PromptTokens,
    long CompletionTokens,
    decimal TotalCost,
    int RequestCount);
