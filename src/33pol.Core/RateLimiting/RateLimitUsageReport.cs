namespace Pol33.Core.RateLimiting;

/// <summary>
/// The usage report served from <c>GET /admin/api/rate-limits/usage</c>: what each caller sent, what
/// was refused, and how close everyone is to their configured limit.
/// </summary>
/// <param name="WindowMinutes">The look-back the counts cover.</param>
/// <param name="GeneratedUtc">When the report was built.</param>
/// <param name="Totals">Gateway-wide roll-up of the same window.</param>
/// <param name="ByTenantModel">
/// The per-user-per-model grid: one row per (tenant, model) pair. This is the section that answers
/// "how does each user use each model".
/// </param>
/// <param name="ByTenant">Per-user load, summed over every model.</param>
/// <param name="ByModel">Per-model load, summed over every user.</param>
/// <param name="ByApiKey">Per-credential load, for finding the one key inside a tenant that is misbehaving.</param>
/// <param name="Violations">Where limits are actually being hit, most-hit first.</param>
/// <param name="Adaptive">What the load-aware governor is currently doing, and why.</param>
/// <param name="Store">Partition-table occupancy, so an operator can see the ceiling approaching.</param>
public sealed record RateLimitUsageReport(
    int WindowMinutes,
    DateTimeOffset GeneratedUtc,
    RateLimitUsageTotals Totals,
    IReadOnlyList<RateLimitUsageRow> ByTenantModel,
    IReadOnlyList<RateLimitUsageRow> ByTenant,
    IReadOnlyList<RateLimitUsageRow> ByModel,
    IReadOnlyList<RateLimitUsageRow> ByApiKey,
    IReadOnlyList<RateLimitViolationRow> Violations,
    AdaptiveRateLimitReport Adaptive,
    RateLimitStoreReport Store)
{
    /// <summary>
    /// Activity per configured limit, busiest first. Counted at the control that was actually
    /// evaluated — never derived from the per-subject rows above.
    /// </summary>
    public IReadOnlyList<RateLimitLimitUsageRow> Limits { get; init; } = [];

    /// <summary>
    /// The two protective scopes. Always both rows, because their counters are reserved and cannot
    /// be crowded out: a zero here means zero.
    /// </summary>
    public IReadOnlyList<RateLimitProtectiveUsageRow> Protective { get; init; } = [];

    /// <summary>Whether the counters behind this report are complete.</summary>
    public RateLimitTrackerReport Tracker { get; init; } = RateLimitTrackerReport.Empty;
}

/// <summary>
/// What one configured limit did in the window. Every counter is a count of <em>decisions by this
/// limit</em>; none is a count of requests to the gateway.
/// </summary>
/// <param name="LimitId">
/// Stable id of the control: a rule identity (<c>scope:target</c>, lower case), <c>plan:&lt;slug&gt;</c>
/// or <c>default</c>.
/// </param>
/// <param name="Scope">The part of <paramref name="LimitId"/> before the first colon.</param>
/// <param name="Target">The part after it; empty for <c>default</c>.</param>
/// <param name="AnonymousBucket">
/// True for the separate bucket a <c>model</c> rule keeps for anonymous callers. Same limit id,
/// different bucket, so it is a row of its own.
/// </param>
/// <param name="SingleBucket">
/// Whether everything counted here drained one bucket. Only then are <paramref name="PeakChargedInOneMinute"/>
/// and <paramref name="PeakUtilization"/> comparable with the limit; a tier's row sums callers that
/// each have a bucket of their own.
/// </param>
/// <param name="Evaluations">Times this limit was asked for a token. A limit placed after one that refused is not asked.</param>
/// <param name="Charged">Evaluations where the token was kept: the request passed every rate limit.</param>
/// <param name="RefusedByRate">Evaluations where this limit's bucket was empty: it is the limit that answered 429.</param>
/// <param name="PassedThenRefunded">
/// Evaluations where this limit gave a token and got it back because another limit refused.
/// Always <c>Evaluations - Charged - RefusedByRate</c>.
/// </param>
/// <param name="StreamsStarted">Streaming responses that took a slot under this limit's concurrency cap.</param>
/// <param name="RefusedByStreams">Streaming requests this limit's concurrency cap refused.</param>
/// <param name="ChargedPerMinute"><paramref name="Charged"/> divided by the window length in minutes.</param>
/// <param name="PeakChargedInOneMinute">The most tokens kept in any one UTC calendar minute of the window.</param>
/// <param name="PeakMinuteUtc">Start of that minute; null when nothing was charged.</param>
/// <param name="ConfiguredRpm">
/// Requests per minute this limit last enforced before adaptive scaling — the scheduled tier when a
/// window was active. Zero until a rate decision has been recorded.
/// </param>
/// <param name="EffectiveRpm">The same after adaptive scaling.</param>
/// <param name="PeakUtilization">
/// <paramref name="PeakChargedInOneMinute"/> over <paramref name="EffectiveRpm"/>. Null unless
/// <paramref name="SingleBucket"/> and a rate is known. May exceed 1: a bucket also holds burst.
/// </param>
/// <param name="LastDecisionUtc">The last time this limit was evaluated, to the second. Null after a reset.</param>
public sealed record RateLimitLimitUsageRow(
    string LimitId,
    string Scope,
    string Target,
    bool AnonymousBucket,
    bool SingleBucket,
    long Evaluations,
    long Charged,
    long RefusedByRate,
    long PassedThenRefunded,
    long StreamsStarted,
    long RefusedByStreams,
    double ChargedPerMinute,
    long PeakChargedInOneMinute,
    DateTimeOffset? PeakMinuteUtc,
    int ConfiguredRpm,
    int EffectiveRpm,
    double? PeakUtilization,
    DateTimeOffset? LastDecisionUtc);

/// <summary>Activity of one protective scope in the window. Every caller has its own bucket.</summary>
/// <remarks>
/// The <c>anonymous</c> row counts only while the anonymous rule sets a rate of its own. Without one,
/// anonymous callers are held to the default tier and are counted under the <c>default</c> limit.
/// </remarks>
/// <param name="Scope"><c>auth_failure</c> or <c>anonymous</c>.</param>
/// <param name="LimitId">The scope's rule identity.</param>
/// <param name="Checked">
/// <c>auth_failure</c>: credentialed requests checked against an address block's failed-credential
/// budget. <c>anonymous</c>: anonymous requests evaluated against the anonymous rate.
/// </param>
/// <param name="Charged">
/// <c>auth_failure</c>: credentials that were then rejected, each debiting one token.
/// <c>anonymous</c>: requests that kept their token.
/// </param>
/// <param name="Refused">Requests this scope answered 429.</param>
/// <param name="EnforcedRpm">
/// The rate last enforced. For <c>auth_failure</c> with no rule configured this is the default
/// tier's rate, which is what the limiter falls back to. Zero until a decision is recorded.
/// </param>
/// <param name="RefusedByStreams"><c>anonymous</c> only: streaming requests its concurrency cap refused.</param>
/// <param name="LastDecisionUtc">The last decision, to the second. Null when there has been none.</param>
public sealed record RateLimitProtectiveUsageRow(
    string Scope,
    string LimitId,
    long Checked,
    long Charged,
    long Refused,
    int EnforcedRpm,
    long RefusedByStreams,
    DateTimeOffset? LastDecisionUtc);

/// <summary>Completeness of the in-memory counters. Process-local; resets with the process.</summary>
/// <param name="TrackingSinceUtc">When counting started: process start or the last reset.</param>
/// <param name="MaxKeysPerDimension">The ceiling each dimension is held to.</param>
/// <param name="IsSaturated">
/// True when at least one decision since <paramref name="TrackingSinceUtc"/> was not counted because
/// a dimension was full. Absence of a row then does not mean absence of traffic.
/// </param>
/// <param name="Dimensions">One entry per section of the report.</param>
public sealed record RateLimitTrackerReport(
    DateTimeOffset? TrackingSinceUtc,
    int MaxKeysPerDimension,
    bool IsSaturated,
    IReadOnlyList<RateLimitTrackerDimension> Dimensions)
{
    public static RateLimitTrackerReport Empty { get; } = new(null, 0, false, []);
}

/// <param name="Name"><c>tenants</c>, <c>models</c>, <c>apiKeys</c>, <c>tenantModels</c>, <c>violations</c> or <c>limits</c>.</param>
/// <param name="TrackedKeys">Keys currently held.</param>
/// <param name="MaxKeys">Ceiling for this dimension.</param>
/// <param name="AtCapacity">No new key can be added. Not by itself a loss: see <paramref name="DroppedDecisions"/>.</param>
/// <param name="DroppedDecisions">
/// Decisions not counted in this dimension because their key was new and the dimension was full.
/// Decisions, not subjects: which subjects were turned away is exactly what is not stored.
/// </param>
/// <param name="FirstDroppedUtc">When the first one was dropped; null when none has been.</param>
public sealed record RateLimitTrackerDimension(
    string Name,
    int TrackedKeys,
    int MaxKeys,
    bool AtCapacity,
    long DroppedDecisions,
    DateTimeOffset? FirstDroppedUtc);

/// <summary>Per-minute history from the same counters as the usage report.</summary>
/// <param name="Subject"><c>gateway</c>, or <c>limit</c> when <paramref name="LimitId"/> is set.</param>
/// <param name="LimitId">The limit the points describe; null for the gateway-wide series.</param>
/// <param name="AnonymousBucket">Which of a model rule's two buckets; false otherwise.</param>
/// <param name="BucketMinutes">Width of every point. Buckets are aligned to the Unix epoch in UTC.</param>
/// <param name="FromUtc">Start of the first bucket.</param>
/// <param name="ToUtc">End of the last bucket, which is the one in progress.</param>
/// <param name="TrackingSinceUtc">Counting began here. Buckets that end before it are not covered.</param>
/// <param name="Points">Oldest first, contiguous, at most 180.</param>
public sealed record RateLimitUsageSeries(
    string Subject,
    string? LimitId,
    bool AnonymousBucket,
    int BucketMinutes,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset? TrackingSinceUtc,
    IReadOnlyList<RateLimitUsagePoint> Points);

/// <param name="StartUtc">Start of the bucket, UTC.</param>
/// <param name="Covered">
/// False when the bucket ended before counting began, so its zeros are "not observed" rather than
/// "nothing happened". A covered bucket with zeros is a real zero.
/// </param>
/// <param name="Decisions">Gateway: admission decisions. Limit: evaluations of that limit.</param>
/// <param name="Admitted">Gateway: decisions that admitted. Limit: tokens kept (charged).</param>
/// <param name="RefusedByRate">Refusals from a token bucket.</param>
/// <param name="RefusedByStreams">Refusals from a concurrency cap.</param>
public sealed record RateLimitUsagePoint(
    DateTimeOffset StartUtc,
    bool Covered,
    long Decisions,
    long Admitted,
    long RefusedByRate,
    long RefusedByStreams);

/// <param name="Requests">Admission decisions made in the window.</param>
/// <param name="Admitted">Decisions that let the request through.</param>
/// <param name="Rejected">Decisions that refused it.</param>
/// <param name="RateRejected">Refusals from a token bucket.</param>
/// <param name="ConcurrencyRejected">Refusals from a concurrency cap.</param>
public sealed record RateLimitUsageTotals(
    long Requests,
    long Admitted,
    long Rejected,
    long RateRejected,
    long ConcurrencyRejected)
{
    /// <summary>Share of decisions that were refusals, in <c>[0, 1]</c>.</summary>
    public double RejectionRate => Requests == 0 ? 0 : (double)Rejected / Requests;
}

/// <param name="Key">The row's identity — a tenant id, a model id, an API key id, or "tenant|model".</param>
/// <param name="TenantId">The tenant, when the row has one.</param>
/// <param name="ApiKeyId">The API key, when the row has one.</param>
/// <param name="ModelId">The model, when the row has one.</param>
/// <param name="Requests">Admission decisions in the window.</param>
/// <param name="Admitted">How many were let through.</param>
/// <param name="Rejected">How many were refused.</param>
/// <param name="RequestsPerMinute">Observed rate over the window — the "load" column.</param>
/// <param name="ConfiguredRpm">
/// The tier this row is held to, when one scope clearly owns it. Zero when the row aggregates rows
/// governed by different tiers, where a single limit number would be a fiction.
/// </param>
/// <param name="EffectiveRpm">What was enforced after adaptation; equal to <paramref name="ConfiguredRpm"/> when nothing was adapted.</param>
public sealed record RateLimitUsageRow(
    string Key,
    string? TenantId,
    string? ApiKeyId,
    string? ModelId,
    long Requests,
    long Admitted,
    long Rejected,
    double RequestsPerMinute,
    int ConfiguredRpm,
    int EffectiveRpm)
{
    /// <summary>
    /// Observed rate as a share of the limit in force, in <c>[0, 1+]</c>. Null when the row has no
    /// single governing limit. Above 1 is normal and expected for a row that is being refused: the
    /// numerator counts attempts, not admissions.
    /// </summary>
    public double? Utilization =>
        EffectiveRpm <= 0 ? null : RequestsPerMinute / EffectiveRpm;
}

/// <param name="Scope">The scope whose limit was hit.</param>
/// <param name="Key">The partition inside that scope.</param>
/// <param name="Control">Whether it was the rate bucket or a concurrency cap.</param>
/// <param name="Hits">How many requests it refused in the window.</param>
public sealed record RateLimitViolationRow(
    string Scope,
    string Key,
    string Control,
    long Hits);

/// <param name="Enabled">Whether load-aware adaptation is switched on.</param>
/// <param name="LastEvaluatedUtc">When factors were last recomputed.</param>
/// <param name="BackedOffPartitions">Partitions currently being told to wait longer than the bucket alone would say.</param>
/// <param name="Models">Per-model factor, saturation and the reason it last moved.</param>
public sealed record AdaptiveRateLimitReport(
    bool Enabled,
    DateTimeOffset? LastEvaluatedUtc,
    int BackedOffPartitions,
    IReadOnlyList<AdaptiveModelRow> Models);

public sealed record AdaptiveModelRow(
    string ModelId,
    double Factor,
    double Saturation,
    string Reason,
    DateTimeOffset UpdatedUtc);

/// <param name="RequestPartitions">Live token buckets.</param>
/// <param name="StreamPartitions">Live concurrency-slot states.</param>
/// <param name="MaxPartitions">The ceiling each dimension is held to; approaching it means evictions.</param>
public sealed record RateLimitStoreReport(
    int RequestPartitions,
    int StreamPartitions,
    int MaxPartitions);
