using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IDailyUsageRollupAggregator
{
    IReadOnlyList<DailyUsageRollupRecord> Aggregate(IEnumerable<BillingEventRecord> events);
}
