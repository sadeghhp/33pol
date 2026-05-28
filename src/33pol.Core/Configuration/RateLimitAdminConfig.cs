namespace Pol33.Core.Configuration;

/// <summary>Admin-visible rate-limit configuration (default tier + plan tiers).</summary>
public sealed class RateLimitAdminConfig
{
    public RateLimitTierOptions Default { get; init; } = new();

    public IReadOnlyDictionary<string, RateLimitTierOptions> Plans { get; init; } =
        new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase);
}
