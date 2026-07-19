using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class RateLimitPolicyResolver(IGatewayConfigProvider configProvider) : IRateLimitPolicyResolver
{
    public RateLimitPolicy Resolve(string? planSlug, string? tenantSlug)
    {
        var rateLimits = configProvider.Current.RateLimits;

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

    private static RateLimitPolicy Clamp(RateLimitPolicy tier) =>
        new(
            Math.Max(1, tier.Rpm),
            Math.Max(0, tier.Burst),
            Math.Max(0, tier.MaxConcurrentStreams));
}
