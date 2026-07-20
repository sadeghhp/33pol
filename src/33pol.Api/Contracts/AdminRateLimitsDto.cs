using Pol33.Core.Configuration;

namespace Pol33.Api.Contracts;

/// <summary>
/// GET response and PUT request body for <c>/admin/api/rate-limits</c> (default + plan tiers only).
/// </summary>
public sealed class AdminRateLimitsDto
{
    /// <summary>
    /// Global master switch. When false the gateway enforces no request-rate or stream-concurrency
    /// limits. Tier values below are still persisted so the configuration survives a disable/enable cycle.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
