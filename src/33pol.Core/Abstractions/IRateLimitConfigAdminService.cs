using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IRateLimitConfigAdminService
{
    RateLimitAdminConfig GetCurrent();

    /// <param name="enabled">Global master switch; false disables all rate-limit enforcement.</param>
    /// <param name="adaptiveEnabled">Whether load-aware adaptation may reduce the configured tiers.</param>
    /// <param name="rules">
    /// The complete scoped rule set. Null leaves the stored rules untouched, so a client written
    /// against the older contract — which had no notion of scoped rules — updates the tiers it knows
    /// about without silently deleting rules it cannot see.
    /// </param>
    Task<RateLimitConfigUpdateResult> UpdateAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        IReadOnlyList<RateLimitRuleDefinition>? rules = null,
        CancellationToken cancellationToken = default);
}
