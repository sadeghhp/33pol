namespace Pol33.Core.Configuration;

/// <summary>Settings for the admin Overview page (<c>Gateway:Overview</c>).</summary>
public sealed class OverviewOptions
{
    public OverviewWindowedStatsOptions WindowedStats { get; set; } = new();

    /// <summary>
    /// How long a database-backed Overview section (FinOps, policy, control plane, activity,
    /// tenants) is served from memory before it is rebuilt.
    /// </summary>
    public int SlowSectionTtlSeconds { get; set; } = 15;

    public OverviewAttentionOptions Attention { get; set; } = new();
}

public sealed class OverviewWindowedStatsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Upper bound on models with their own 24-hour ring. Each ring costs roughly half a megabyte;
    /// models past the cap still count toward the gateway-wide windows.
    /// </summary>
    public int MaxTrackedModels { get; set; } = 64;
}

/// <summary>
/// Thresholds for the Overview's Attention list. Defaults mirror the Prometheus rules shipped under
/// <c>deploy/prometheus/alerts</c>, so the in-app list and the alerting stack agree.
/// </summary>
public sealed class OverviewAttentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Error rate over the 5-minute window that raises a warning (0.05 = 5%).</summary>
    public double ErrorRateWarn { get; set; } = 0.05;

    /// <summary>Minimum requests in the window before an error rate is judged at all.</summary>
    public int ErrorRateMinRequests { get; set; } = 20;

    /// <summary>How long a condition must hold before it is listed (the Prometheus <c>for</c>).</summary>
    public int ErrorRateForSeconds { get; set; } = 300;

    public int CircuitOpenForSeconds { get; set; } = 300;

    public int BackendUnhealthyForSeconds { get; set; } = 120;

    public int UsageWriterQueueWarn { get; set; } = 5_000;

    public double UsageParseFailureRatePerSecondWarn { get; set; } = 0.1;

    public int ReconciliationStalledAfterMinutes { get; set; } = 180;

    /// <summary>Budget spend ratio that is listed even below the budget's own warning threshold.</summary>
    public double BudgetNearLimitRatio { get; set; } = 0.9;

    public int KeyExpiringWithinDays { get; set; } = 7;

    public int KeyIdleAfterDays { get; set; } = 30;

    public int BackupStaleAfterDays { get; set; } = 7;

    /// <summary>Share of rate-limit decisions refused over the last 5 minutes that raises a warning (0.10 = 10%).</summary>
    public double RateLimitRefusalShareWarn { get; set; } = 0.10;

    /// <summary>Minimum rate-limit decisions in the 5-minute window before the refusal share is judged at all.</summary>
    public int RateLimitRefusalMinDecisions { get; set; } = 20;

    public int RateLimitRefusalForSeconds { get; set; } = 300;

    /// <summary>
    /// Partition-table fill over its ceiling above which a warning is raised. Must match the threshold in
    /// the Prometheus rule <c>GatewayRateLimitPartitionsNearCeiling</c> (a test holds the two together).
    /// </summary>
    public double RateLimitPartitionsNearCeilingRatio { get; set; } = 0.8;

    /// <summary>The same rule's <c>for</c> (10m).</summary>
    public int RateLimitPartitionsNearCeilingForSeconds { get; set; } = 600;

    /// <summary>
    /// How long rules must exist with enforcement switched off before it is listed, so a save or reload
    /// that is still being applied is not reported as a settled state.
    /// </summary>
    public int RateLimitNotEnforcedForSeconds { get; set; } = 60;
}
