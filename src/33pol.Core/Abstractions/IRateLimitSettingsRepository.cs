using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists the database-backed rate-limit configuration (the default tier and the per-plan tiers).
/// Registered only when a database connection string is configured.
/// </summary>
public interface IRateLimitSettingsRepository
{
    /// <summary>
    /// Replaces the global enabled flag, the default tier and the full set of plan tiers, and bumps the
    /// config version, in a single atomic write. Plan slugs are stored as given; the snapshot compares
    /// them OrdinalIgnoreCase.
    /// </summary>
    /// <param name="enabled">
    /// Global master switch. Tier values are still persisted when false, so disabling and re-enabling
    /// round-trips without losing the configured numbers.
    /// </param>
    Task SaveAsync(
        bool enabled,
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        CancellationToken cancellationToken = default);
}
