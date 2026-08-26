using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IRateLimitPolicyResolver
{
    /// <summary>
    /// The tier a tenant is held to. <paramref name="tenantId"/> is what the request carries and
    /// what the bucket is keyed on; <paramref name="tenantSlug"/> is the spelling an operator is
    /// more likely to have written a per-tenant rule against. Both are matched.
    /// </summary>
    RateLimitPolicy Resolve(string? planSlug, string? tenantId, string? tenantSlug);

    /// <summary>
    /// Whether rate limiting is enforced at all. Read per request from the live config snapshot, so
    /// toggling it in the admin UI takes effect without a restart. Callers must check this before
    /// acquiring request or stream-concurrency slots.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// The tier applied to requests authentication refuses, counted per client address block.
    /// </summary>
    /// <remarks>
    /// Its own tier because the default one is the wrong shape for credential guessing: a default
    /// sized for legitimate traffic lets one address make hundreds of guesses a minute, which is not
    /// a limit on guessing in any useful sense. The default body returns the default tier, which is
    /// what the gateway enforced before the setting existed.
    /// </remarks>
    RateLimitPolicy ResolveAuthFailure() => Resolve(planSlug: null, tenantId: null, tenantSlug: null);
}
