namespace Pol33.Core.Configuration;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public string DefaultCurrency { get; set; } = "USD";

    public decimal DefaultWarningThresholdRatio { get; set; } = 0.8m;

    public int UsageRetentionDays { get; set; } = 90;
}
