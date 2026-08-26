using Pol33.Core.RateLimiting;

namespace Pol33.Core.Configuration;

/// <summary>Admin-visible rate-limit configuration: the default tier, plan tiers and scoped rules.</summary>
public sealed class RateLimitAdminConfig
{
    /// <summary>Global master switch; false means no rate limiting is enforced.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether load-aware adaptation may reduce the configured tiers. Independent of
    /// <see cref="Enabled"/>, so the adaptive half can be switched off without switching off
    /// enforcement.
    /// </summary>
    public bool AdaptiveEnabled { get; init; }

    public RateLimitTierOptions Default { get; init; } = new();

    public IReadOnlyDictionary<string, RateLimitTierOptions> Plans { get; init; } =
        new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-model, per-key and combined rules, plus the global and auth-failure singletons.</summary>
    public IReadOnlyList<RateLimitRuleDefinition> Rules { get; init; } = [];
}
