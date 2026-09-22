namespace Pol33.Core.Models.Overview;

/// <summary>
/// Rate limiting at a glance: whether it is enforced, what it refused in the last hour and the last
/// five minutes, which limits did the refusing, and how far to trust the counters behind it.
/// </summary>
/// <remarks>
/// Built from the in-memory usage tracker, which is process-local and keeps about three hours of
/// per-minute counters (<see cref="Retention"/>): nothing here is durable history, and every figure
/// restarts with the gateway. A gateway without the tracker has no such section at all (204); one
/// with the tracker and no traffic gets this record with zeros, which are real zeros.
/// </remarks>
public sealed record RateLimitOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    /// <summary>The master switch as the request path reads it. False means nothing is rate limited.</summary>
    public bool Enforced { get; init; }

    public bool AdaptiveEnabled { get; init; }

    /// <summary>Ordinary scoped rules (global, tenant, key, model and the two pairs); the protective budgets are not counted.</summary>
    public int RuleCount { get; init; }

    /// <summary>Rules an operator switched off; included in <see cref="RuleCount"/>.</summary>
    public int DisabledRuleCount { get; init; }

    /// <summary>True while a configuration reload is being applied, so a momentary state is not judged as settled.</summary>
    public bool ConfigReloadInProgress { get; init; }

    public RateLimitScheduleOverview Schedule { get; init; } = RateLimitScheduleOverview.Unavailable;

    public RateLimitRetentionOverview Retention { get; init; } = new();

    public RateLimitRefusalWindow LastHour { get; init; } = new();

    public RateLimitRefusalWindow LastFiveMinutes { get; init; } = new();

    /// <summary>Tenants with at least one refusal in the last hour.</summary>
    public int RefusedTenantCount { get; init; }

    /// <summary>API keys with at least one refusal in the last hour.</summary>
    public int RefusedKeyCount { get; init; }

    /// <summary>The subject lists hit the report's row cap, so the counts are lower bounds.</summary>
    public bool RefusedSubjectsTruncated { get; init; }

    public IReadOnlyList<RateLimitRefusedSubject> TopRefusedTenants { get; init; } = [];

    public IReadOnlyList<RateLimitRefusedSubject> TopRefusedKeys { get; init; } = [];

    /// <summary>Configured limits that refused at least once in the last hour.</summary>
    public int RefusingLimitCount { get; init; }

    /// <summary>Limits that refused, then limits near their rate, most refused first; at most six.</summary>
    public IReadOnlyList<RateLimitLimitOverview> Limits { get; init; } = [];

    /// <summary>The two protective budgets, <c>auth_failure</c> then <c>anonymous</c>; always both.</summary>
    public IReadOnlyList<RateLimitProtectiveOverview> Protective { get; init; } = [];

    public RateLimitAdaptiveOverview Adaptive { get; init; } = new();

    public RateLimitTrackerOverview Tracker { get; init; } = new();

    public RateLimitStoreOverview Store { get; init; } = new();
}

/// <summary>How much history the counters hold and since when. Process-local: a restart starts over.</summary>
/// <param name="HistoryMinutes">Per-minute counters kept, oldest dropped first (about three hours).</param>
/// <param name="TrackingSinceUtc">Counting began here: process start or the last reset.</param>
/// <param name="ProcessLocal">Always true: the counters are in memory and are neither persisted nor shared between replicas.</param>
public sealed record RateLimitRetentionOverview(int HistoryMinutes = 0, DateTimeOffset? TrackingSinceUtc = null, bool ProcessLocal = true);

/// <summary>Admission decisions in one trailing window.</summary>
/// <param name="RefusalShare">Refused over decisions in <c>[0, 1]</c>; zero when there were no decisions.</param>
public sealed record RateLimitRefusalWindow(
    int Minutes = 0,
    long Decisions = 0,
    long Admitted = 0,
    long Refused = 0,
    long RefusedByRate = 0,
    long RefusedByStreams = 0,
    double RefusalShare = 0);

/// <summary>What the schedule says now, from the same computation as the rate-limits calendar.</summary>
/// <param name="Available">False when the schedule could not be read; the counts are then unknown, not zero.</param>
/// <param name="ScheduledRuleCount">Enabled rules that carry at least one window.</param>
/// <param name="WindowsActiveNow">Rules whose tier is currently set by a window.</param>
/// <param name="NextChangeAtUtc">The soonest future moment any rule's tier changes; null when none is scheduled.</param>
/// <param name="NextChangeRuleId">That rule's <c>scope:target</c> identity, lower case.</param>
/// <param name="NextChangeWindow">The window that starts then, when the change is a start.</param>
public sealed record RateLimitScheduleOverview(
    bool Available,
    int ScheduledRuleCount,
    int WindowsActiveNow,
    DateTimeOffset? NextChangeAtUtc,
    string? NextChangeRuleId,
    string? NextChangeWindow)
{
    public static RateLimitScheduleOverview Unavailable { get; } = new(false, 0, 0, null, null, null);
}

/// <summary>One tenant or API key refused in the last hour.</summary>
/// <param name="Key">The tracker's key: a tenant id, the anonymous partition, or an API key id.</param>
/// <param name="Label">Tenant slug or key label (else its public prefix); null when it could not be resolved.</param>
/// <param name="TenantSlug">For a key, the tenant that owns it, when known.</param>
/// <param name="Anonymous">True for an unauthenticated caller's partition.</param>
public sealed record RateLimitRefusedSubject(
    string Key,
    string? Label,
    string? TenantSlug,
    bool Anonymous,
    long Decisions,
    long Refused);

/// <summary>One configured limit's own decisions in the last hour.</summary>
/// <param name="LimitId">Stable id: a rule identity (<c>scope:target</c>, lower case), <c>plan:&lt;slug&gt;</c> or <c>default</c>.</param>
/// <param name="RuleId">
/// <paramref name="LimitId"/> when the limit is a scoped rule that can be opened in the rule list; null
/// for plan tiers and the default tier, which are edited as baselines.
/// </param>
/// <param name="SingleBucket">Whether everything counted drained one bucket; only then is <paramref name="PeakUtilization"/> meaningful.</param>
/// <param name="Refused"><paramref name="RefusedByRate"/> plus <paramref name="RefusedByStreams"/>.</param>
/// <param name="PeakUtilization">
/// Busiest minute over the enforced rate. Null unless the limit counts a single bucket and a rate is
/// known — a tier's row sums many callers' buckets and has no meaningful percentage.
/// </param>
public sealed record RateLimitLimitOverview(
    string LimitId,
    string? RuleId,
    string Scope,
    string Target,
    bool AnonymousBucket,
    bool SingleBucket,
    long Evaluations,
    long Refused,
    long RefusedByRate,
    long RefusedByStreams,
    int ConfiguredRpm,
    int EffectiveRpm,
    double? PeakUtilization,
    DateTimeOffset? LastDecisionUtc);

/// <param name="Scope"><c>auth_failure</c> or <c>anonymous</c>.</param>
public sealed record RateLimitProtectiveOverview(
    string Scope,
    long Checked,
    long Refused,
    int EnforcedRpm,
    DateTimeOffset? LastDecisionUtc);

/// <param name="ModelsReduced">Models whose factor is below 1, i.e. being enforced below their configured rate.</param>
/// <param name="LowestFactor">The smallest factor in force; null when no model is reduced.</param>
public sealed record RateLimitAdaptiveOverview(
    bool Enabled = false,
    int ModelsReduced = 0,
    double? LowestFactor = null,
    string? LowestFactorModelId = null,
    int BackedOffPartitions = 0,
    DateTimeOffset? LastEvaluatedUtc = null)
{
    /// <summary>Load shedding is actively reducing at least one model's rate.</summary>
    public bool Shedding => Enabled && ModelsReduced > 0;
}

/// <param name="IsSaturated">At least one decision was not counted because a dimension was full; a missing row is then unknown, not zero.</param>
public sealed record RateLimitTrackerOverview(
    bool IsSaturated = false,
    long DroppedDecisions = 0,
    DateTimeOffset? FirstDroppedUtc = null,
    int MaxKeysPerDimension = 0)
{
    /// <summary>Dimensions that cannot take a new key (tenants, models, apiKeys, tenantModels, violations, limits).</summary>
    public IReadOnlyList<string> AtCapacity { get; init; } = [];
}

/// <param name="Ratio">
/// The fuller of the two partition tables over the ceiling (floored at one) — the exact expression
/// of the Prometheus alert <c>GatewayRateLimitPartitionsNearCeiling</c>.
/// </param>
public sealed record RateLimitStoreOverview(
    int RequestPartitions = 0,
    int StreamPartitions = 0,
    int MaxPartitions = 0,
    double Ratio = 0);
