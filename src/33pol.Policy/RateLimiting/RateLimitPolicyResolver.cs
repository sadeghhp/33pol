using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class RateLimitPolicyResolver : IRateLimitPolicyResolver
{
    private readonly IOptionsMonitor<RateLimitingOptions> _options;

    public RateLimitPolicyResolver(IOptionsMonitor<RateLimitingOptions> options) =>
        _options = options;

    public RateLimitPolicy Resolve(string? planSlug, string? tenantSlug)
    {
        var options = _options.CurrentValue;

        if (!string.IsNullOrWhiteSpace(tenantSlug) &&
            options.Tenants.TryGetValue(tenantSlug, out var tenantTier))
        {
            return ToPolicy(tenantTier);
        }

        if (!string.IsNullOrWhiteSpace(planSlug) &&
            options.Plans.TryGetValue(planSlug, out var planTier))
        {
            return ToPolicy(planTier);
        }

        return ToPolicy(options.Default);
    }

    private static RateLimitPolicy ToPolicy(RateLimitTierOptions tier) =>
        new(
            Math.Max(1, tier.Rpm),
            Math.Max(0, tier.Burst),
            Math.Max(0, tier.MaxConcurrentStreams));
}
