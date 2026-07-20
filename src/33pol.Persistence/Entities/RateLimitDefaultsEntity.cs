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

    public DateTimeOffset UpdatedAt { get; set; }
}
