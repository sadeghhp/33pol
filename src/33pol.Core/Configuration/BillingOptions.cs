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
    /// Backstop for reclaiming a budget reservation whose request never settled. Every terminal path
    /// in the router now releases explicitly, so this should only ever fire after a process crash.
    ///
    /// It must comfortably exceed the longest possible in-flight request plus the usage-flush delay:
    /// a TTL shorter than that sweeps reservations for requests that are still running, letting
    /// concurrent requests each see full headroom and collectively blow through a hard-stop budget.
    /// <see cref="BillingReservationTtlPolicy"/> derives the safe minimum and configuration
    /// validation rejects anything below it.
    /// </summary>
    public int BudgetReservationTtlSeconds { get; set; } = 900;

    /// <summary>
    /// How long a model's rate card is cached. Budget enforcement prices every request before
    /// forwarding it, so this keeps that off SQLite. Admin edits invalidate the entry immediately
    /// in-process; the TTL bounds staleness across replicas.
    /// </summary>
    public int RateCardCacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// How long a tenant's budget definitions are cached. Budget enforcement runs on every request,
    /// so this keeps the definition lookup off the database. Only definitions are cached — spend is
    /// always read fresh and in-flight cost is covered by the reservation ledger, so this cannot let
    /// a tenant exceed a hard stop.
    /// </summary>
    public int BudgetCacheTtlSeconds { get; set; } = 30;

    /// <summary>
    /// How long a tenant's period-to-date <em>persisted</em> spend is cached.
    /// </summary>
    /// <remarks>
    /// Budget enforcement previously re-read and re-summed every rollup row in the billing period on
    /// every inference request, for every hard-stop budget. Caching cannot let a tenant overshoot:
    /// spend incurred since the last read is tracked exactly by the reservation ledger and added on
    /// top of this figure. The TTL only bounds how long after spend lands in the rollups a hard stop
    /// can take to engage.
    /// </remarks>
    public int BudgetSpendCacheTtlSeconds { get; set; } = 10;
}
