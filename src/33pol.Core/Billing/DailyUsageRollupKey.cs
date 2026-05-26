namespace Pol33.Core.Billing;

public readonly record struct DailyUsageRollupKey(
    DateOnly UsageDate,
    Guid? TenantId,
    string ModelId,
    string? CostCenter)
{
    public static DailyUsageRollupKey FromEvent(BillingEventRecord billingEvent) =>
        new(
            DateOnly.FromDateTime(billingEvent.RecordedAt.UtcDateTime),
            billingEvent.TenantId,
            billingEvent.ModelId,
            NormalizeCostCenter(billingEvent.CostCenter));

    public static DailyUsageRollupKey FromRecord(DailyUsageRollupRecord rollup) =>
        new(
            rollup.UsageDate,
            rollup.TenantId,
            rollup.ModelId,
            NormalizeCostCenter(rollup.CostCenter));

    public static string? NormalizeCostCenter(string? costCenter) =>
        string.IsNullOrWhiteSpace(costCenter) ? null : costCenter.Trim();
}
