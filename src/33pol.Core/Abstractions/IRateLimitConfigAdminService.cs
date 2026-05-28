using Pol33.Core.Configuration;

namespace Pol33.Core.Abstractions;

public interface IRateLimitConfigAdminService
{
    RateLimitAdminConfig GetCurrent();

    Task<RateLimitConfigUpdateResult> UpdateAsync(
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        CancellationToken cancellationToken = default);
}
