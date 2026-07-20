namespace Pol33.Core.Configuration;

/// <summary>Admin-visible rate-limit configuration (default tier + plan tiers).</summary>
public sealed class RateLimitAdminConfig
{
    /// <summary>Global master switch; false means no rate limiting is enforced.</summary>
    public bool Enabled { get; init; } = true;

    public RateLimitTierOptions Default { get; init; } = new();

    public IReadOnlyDictionary<string, RateLimitTierOptions> Plans { get; init; } =
        new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase);
}
