namespace Pol33.Core.Configuration;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public string DefaultCurrency { get; set; } = "USD";

    public decimal DefaultWarningThresholdRatio { get; set; } = 0.8m;

    public int UsageRetentionDays { get; set; } = 90;

    public int UsageWriterBatchSize { get; set; } = 100;

    public int UsageWriterFlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// How many times a batch that failed to persist is retried (on later flush ticks, with a
    /// growing back-off) before it is dropped and counted as lost. 0 disables retries.
    /// </summary>
    public int UsageWriterMaxFlushRetries { get; set; } = 5;

    /// <summary>
    /// Upper bound on buffered usage events while persistence is failing. Past this the oldest
    /// events are shed (and counted as dropped) so an outage cannot grow memory without limit.
    /// Always at least <see cref="UsageWriterBatchSize"/>.
    /// </summary>
    public int UsageWriterMaxPendingEvents { get; set; } = 10_000;

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
    /// <see cref="BillingReservationTtlPolicy"/> derives the safe minimum — including the
    /// body-scaled header allowance a maximum-size request receives — and configuration validation
    /// rejects anything below it. With the default resilience timings (300 s base + 60 s/MB for a
    /// 25 MB body = 1800 s for headers, 120 s streaming, 1 s flush, 60 s margin) the floor is 1981 s.
    /// </summary>
    public int BudgetReservationTtlSeconds { get; set; } = 2400;

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
    /// top of this figure, and the usage writer invalidates a tenant's cached spend the moment a
    /// batch's cost reaches the rollups — before it releases those reservations — so there is no
    /// window in which the cost is counted by neither. The TTL therefore only bounds staleness for
    /// spend written by another replica.
    /// </remarks>
    public int BudgetSpendCacheTtlSeconds { get; set; } = 10;

    /// <summary>
    /// Whether the gateway periodically reconciles the billing event ledger against the daily usage
    /// rollups derived from it. On by default: the comparison is the only thing that makes a
    /// divergence between the two visible, and every known failure in that path is silent.
    /// </summary>
    public bool ReconciliationEnabled { get; set; } = true;

    /// <summary>How often the reconciliation sweep runs.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How many days back each sweep reconciles, ending with yesterday (UTC).
    /// </summary>
    /// <remarks>
    /// Ends at yesterday rather than today because today's rollups are still being written; comparing
    /// a day in progress races the usage writer's flush interval and reports drift that resolves
    /// itself seconds later. The window is clamped to <see cref="UsageRetentionDays"/> at use, since
    /// retention prunes the ledger but not the rollups — reaching past it would report every pruned
    /// day as a discrepancy.
    /// </remarks>
    public int ReconciliationLookbackDays { get; set; } = 3;
}
