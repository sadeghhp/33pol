using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists the database-backed rate-limit configuration: the default tier, the per-plan tiers, and
/// the scoped rules. Registered only when a database connection string is configured.
/// </summary>
public interface IRateLimitSettingsRepository
{
    /// <summary>
    /// Replaces the whole rate-limit configuration and bumps the config version, in a single atomic
    /// write. Plan slugs and rule targets are stored as given; the snapshot compares them
    /// OrdinalIgnoreCase.
    /// </summary>
    /// <param name="enabled">
    /// Global master switch. Tier values are still persisted when false, so disabling and re-enabling
    /// round-trips without losing the configured numbers.
    /// </param>
    /// <param name="adaptiveEnabled">Whether load-aware adaptation may reduce the configured tiers.</param>
    /// <param name="rules">
    /// The complete set of scoped rules. Replaced wholesale rather than merged: a partial update
    /// gives no way to remove a rule, and two admins editing different scopes would each resurrect
    /// what the other deleted.
    /// </param>
    Task SaveAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        IReadOnlyList<RateLimitRuleDefinition> rules,
        CancellationToken cancellationToken = default);
}
