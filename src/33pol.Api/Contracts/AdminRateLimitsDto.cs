using Pol33.Core.Configuration;

namespace Pol33.Api.Contracts;

/// <summary>
/// GET response and PUT request body for <c>/admin/api/rate-limits</c> (default + plan tiers only).
/// </summary>
public sealed class AdminRateLimitsDto
{
    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
