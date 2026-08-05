namespace Pol33.Core.Billing;

/// <summary>
/// An additive change to one daily rollup bucket.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DailyUsageRollupRecord"/>, which carries absolute totals. Persisting
/// usage as a delta lets the storage layer apply it as an atomic increment; computing the new
/// absolute total in application code first is what allowed two concurrent writers to read the same
/// starting value and silently lose one another's usage.
/// </remarks>
public sealed record DailyUsageRollupDelta(
    DateOnly UsageDate,
    Guid? TenantId,
    string ModelId,
    string? CostCenter,
    long PromptTokens,
    long CompletionTokens,
    decimal TotalCost,
    int RequestCount);
