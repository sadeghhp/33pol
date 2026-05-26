using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

internal static class BillingEventMapper
{
    public static BillingEventRecord FromUsageEvent(UsageEvent usageEvent, BillingCostBreakdown? costs = null) =>
        BillingEventFactory.FromUsageEvent(usageEvent, costs);
}
