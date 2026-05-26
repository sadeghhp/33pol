using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IRateLimitPolicyResolver
{
    RateLimitPolicy Resolve(string? planSlug, string? tenantSlug);
}
