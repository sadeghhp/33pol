using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IRateLimitPolicyResolver
{
    RateLimitPolicy Resolve(string? planSlug, string? tenantSlug);

    /// <summary>
    /// Whether rate limiting is enforced at all. Read per request from the live config snapshot, so
    /// toggling it in the admin UI takes effect without a restart. Callers must check this before
    /// acquiring request or stream-concurrency slots.
    /// </summary>
    bool IsEnabled();
}
