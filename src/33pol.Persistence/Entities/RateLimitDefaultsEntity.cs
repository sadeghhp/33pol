namespace Pol33.Persistence.Entities;

/// <summary>Single-row table holding the default rate-limit tier used when no plan/tenant tier matches.</summary>
public sealed class RateLimitDefaultsEntity
{
    /// <summary>Fixed singleton key (always 1).</summary>
    public int Id { get; set; }

    /// <summary>
    /// Global master switch for rate limiting. When false, request-rate and stream-concurrency limits
    /// are not enforced for any tier. Stored on the defaults row because the switch is gateway-wide,
    /// not per-plan.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int Rpm { get; set; }

    public int Burst { get; set; }

    public int MaxConcurrentStreams { get; set; }

    /// <summary>
    /// Whether load-aware adaptation may reduce the configured tiers. Separate from
    /// <see cref="Enabled"/> so an operator can switch off the clever half without switching off
    /// enforcement — the order you want in an incident. Defaults to false: a gateway enforces
    /// exactly what it was configured to until someone asks for something cleverer.
    /// </summary>
    public bool AdaptiveEnabled { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
