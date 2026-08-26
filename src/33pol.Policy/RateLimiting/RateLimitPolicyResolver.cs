using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class RateLimitPolicyResolver(IGatewayConfigProvider configProvider) : IRateLimitPolicyResolver
{
    public bool IsEnabled() => configProvider.Current.RateLimits.Enabled;

    public RateLimitPolicy Resolve(string? planSlug, string? tenantSlug) =>
        ResolveTenantTier(configProvider.Current.RateLimits, planSlug, tenantSlug);

    public RateLimitPolicy ResolveAuthFailure() =>
        ResolveAuthFailureTier(configProvider.Current.RateLimits);

    /// <summary>
    /// The tier applied to a tenant, in the one place precedence exists: a per-tenant override wins
    /// over the tenant's plan, which wins over the default.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="RateLimitPlanResolver"/> so the tenant scope of a rule set and the
    /// standalone tier lookup can never drift apart — two implementations of the same precedence
    /// would eventually disagree, and the symptom would be one middleware admitting what the next
    /// one refuses.
    /// </remarks>
    internal static RateLimitPolicy ResolveTenantTier(
        Core.Configuration.RateLimitsConfigSection rateLimits,
        string? planSlug,
        string? tenantSlug)
    {
        if (!string.IsNullOrWhiteSpace(tenantSlug) &&
            rateLimits.TenantOverrides.TryGetValue(tenantSlug, out var tenantTier))
        {
            return Clamp(tenantTier);
        }

        if (!string.IsNullOrWhiteSpace(planSlug) &&
            rateLimits.Plans.TryGetValue(planSlug, out var planTier))
        {
            return Clamp(planTier);
        }

        return Clamp(rateLimits.Default);
    }

    /// <summary>
    /// The tier for requests authentication refuses. Falls back to the default tier when no
    /// auth-failure tier is configured, which is what deployments that predate the setting get.
    /// </summary>
    internal static RateLimitPolicy ResolveAuthFailureTier(Core.Configuration.RateLimitsConfigSection rateLimits) =>
        rateLimits.AuthFailure.EnforcesRate
            ? Clamp(rateLimits.AuthFailure)
            : Clamp(rateLimits.Default);

    /// <summary>
    /// Floors the default and tenant tiers at 1 rpm. Zero is the "this scope does not limit the
    /// rate" value for the optional scopes, but the tenant scope is the gateway's only universal
    /// limit — reading a zero there as "unlimited" would turn a misconfiguration into no enforcement
    /// at all, silently.
    /// </summary>
    private static RateLimitPolicy Clamp(RateLimitPolicy tier) =>
        new(
            Math.Max(1, tier.Rpm),
            Math.Max(0, tier.Burst),
            Math.Max(0, tier.MaxConcurrentStreams));
}
