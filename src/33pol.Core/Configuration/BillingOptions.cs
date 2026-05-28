namespace Pol33.Core.Configuration;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public string DefaultCurrency { get; set; } = "USD";

    public decimal DefaultWarningThresholdRatio { get; set; } = 0.8m;

    public int UsageRetentionDays { get; set; } = 90;

    public int UsageWriterBatchSize { get; set; } = 100;

    public int UsageWriterFlushIntervalMs { get; set; } = 1000;

    public int BudgetWarningTrackerRetentionLimit { get; set; } = 100_000;

    public int DailyWebhookTrackerRetentionLimit { get; set; } = 100_000;

    /// <summary>UTC hour (0–23) when the scheduled <c>usage.daily</c> webhook runs for the previous day.</summary>
    public int DailyWebhookUtcHour { get; set; } = 1;

    /// <summary>How often the background loop checks whether to send scheduled daily webhooks.</summary>
    public int DailyWebhookPollIntervalSeconds { get; set; } = 900;
}
