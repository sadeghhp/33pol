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

    /// <summary>
    /// Assumed max output tokens when a request does not specify <c>max_tokens</c>, used to estimate
    /// the reserved cost for hard-stop budget enforcement.
    /// </summary>
    public int BudgetReservationDefaultMaxTokens { get; set; } = 4096;

    /// <summary>
    /// How long a budget reservation is held before it is reclaimed if the request never persists
    /// usage (e.g. an upstream error). Prevents leaked reservations from permanently reducing a
    /// tenant's available budget.
    /// </summary>
    public int BudgetReservationTtlSeconds { get; set; } = 120;

    /// <summary>
    /// How long a model's rate card is cached. Budget enforcement prices every request before
    /// forwarding it, so this keeps that off SQLite. Admin edits invalidate the entry immediately
    /// in-process; the TTL bounds staleness across replicas.
    /// </summary>
    public int RateCardCacheTtlSeconds { get; set; } = 60;
}
