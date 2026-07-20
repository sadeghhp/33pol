using Pol33.Core.Configuration;

namespace Pol33.Core.Abstractions;

public interface IRateLimitConfigAdminService
{
    RateLimitAdminConfig GetCurrent();

    /// <param name="enabled">Global master switch; false disables all rate-limit enforcement.</param>
    Task<RateLimitConfigUpdateResult> UpdateAsync(
        bool enabled,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        CancellationToken cancellationToken = default);
}
