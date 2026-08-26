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
    RateLimitStoreReport Store);

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
