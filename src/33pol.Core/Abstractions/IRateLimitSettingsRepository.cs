using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists the database-backed rate-limit configuration (the default tier and the per-plan tiers).
/// Registered only when a database connection string is configured.
/// </summary>
public interface IRateLimitSettingsRepository
{
    /// <summary>
    /// Replaces the default tier and the full set of plan tiers, and bumps the config version, in a
    /// single atomic write. Plan slugs are stored as given; the snapshot compares them
    /// OrdinalIgnoreCase.
    /// </summary>
    Task SaveAsync(
        RateLimitPolicy defaultTier,
        IReadOnlyDictionary<string, RateLimitPolicy> plans,
        CancellationToken cancellationToken = default);
}
