using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Records every admission decision so the gateway can answer "who is using what, and where are they
/// hitting their limits" — per user, per model, and per user-and-model together.
/// </summary>
/// <remarks>
/// <para>In-memory and bounded, deliberately. The gateway is a single process writing to one
/// embedded database, and admission decisions arrive at request rate — persisting one row per
/// decision would put a write on the hot path and make the busiest partition the most expensive one
/// to meter. Instead each tracked key keeps a fixed ring of per-minute counters, so a read is
/// O(keys) and a write is O(1) with no allocation, and the memory is the same whether the gateway is
/// idle or saturated.</para>
///
/// <para>The trade is that the report resets with the process and looks back hours rather than
/// months. Long-horizon usage already has a home — the billing rollups, which are durable and record
/// tokens and cost per tenant and model. This tracker answers the question those cannot: how each
/// caller sits against its <em>limits</em> right now.</para>
/// </remarks>
public interface IRateLimitUsageTracker
{
    /// <summary>Records one decision. Called from the request path, so it must not allocate or block.</summary>
    void Record(in RateLimitUsageEvent usageEvent);

    /// <summary>
    /// Builds the usage report over the last <paramref name="minutes"/> minutes.
    /// </summary>
    /// <param name="take">Maximum rows per section, ordered by request volume.</param>
    RateLimitUsageReport BuildReport(int minutes, int take, DateTimeOffset now);

    /// <summary>Drops every counter. Used by the admin "reset stats" action.</summary>
    void Reset();
}

/// <param name="TenantId">The tenant, or the anonymous partition key for unauthenticated traffic.</param>
/// <param name="ApiKeyId">The credential, or null when anonymous.</param>
/// <param name="ModelId">The model, or null when the request was refused before it was known.</param>
/// <param name="Admitted">Whether the request was let through.</param>
/// <param name="Scope">On a rejection, the scope that refused; on an admission, the tightest scope.</param>
/// <param name="Control">Whether the decision came from the rate bucket or a concurrency cap.</param>
/// <param name="ConfiguredRpm">The tier's configured sustained rate, for the "usage against limit" column.</param>
/// <param name="EffectiveRpm">What was actually enforced, after adaptation.</param>
public readonly record struct RateLimitUsageEvent(
    string? TenantId,
    string? ApiKeyId,
    string? ModelId,
    bool Admitted,
    RateLimitScope? Scope,
    RateLimitControl Control,
    int ConfiguredRpm,
    int EffectiveRpm);
