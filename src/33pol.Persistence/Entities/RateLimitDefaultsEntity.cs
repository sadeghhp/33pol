namespace Pol33.Persistence.Entities;

/// <summary>Single-row table holding the default rate-limit tier used when no plan/tenant tier matches.</summary>
public sealed class RateLimitDefaultsEntity
{
    /// <summary>Fixed singleton key (always 1).</summary>
    public int Id { get; set; }

    public int Rpm { get; set; }

    public int Burst { get; set; }

    public int MaxConcurrentStreams { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
