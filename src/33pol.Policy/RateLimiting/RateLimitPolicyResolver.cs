using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class RateLimitPolicyResolver : IRateLimitPolicyResolver
{
    private readonly RateLimitingOptions _options;

    public RateLimitPolicyResolver(IOptions<RateLimitingOptions> options) =>
        _options = options.Value;

    public RateLimitPolicy Resolve(string? planSlug, string? tenantSlug)
    {
        if (!string.IsNullOrWhiteSpace(tenantSlug) &&
            _options.Tenants.TryGetValue(tenantSlug, out var tenantTier))
        {
            return ToPolicy(tenantTier);
        }

        if (!string.IsNullOrWhiteSpace(planSlug) &&
            _options.Plans.TryGetValue(planSlug, out var planTier))
        {
            return ToPolicy(planTier);
        }

        return ToPolicy(_options.Default);
    }

    private static RateLimitPolicy ToPolicy(RateLimitTierOptions tier) =>
        new(
            Math.Max(1, tier.Rpm),
            Math.Max(0, tier.Burst),
            Math.Max(0, tier.MaxConcurrentStreams));
}
