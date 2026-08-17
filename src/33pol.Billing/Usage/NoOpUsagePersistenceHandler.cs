using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Usage;

/// <summary>
/// The no-database handler. Nothing is stored, but the live feed still learns that the request will
/// never be priced — otherwise every row on a store-less gateway would read "pricing…" forever.
/// </summary>
public sealed class NoOpUsagePersistenceHandler(IRecentRequestStore? recentRequests = null) : IUsagePersistenceHandler
{
    public ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        if (usageEvent is not null && !string.IsNullOrEmpty(usageEvent.RequestId))
        {
            recentRequests?.AttachUsage(
                usageEvent.RequestId,
                RecentRequestUsageMapper.FromUsageEvent(usageEvent, RecentRequestUsage.StatusUnpriced));
        }

        return ValueTask.CompletedTask;
    }
}
