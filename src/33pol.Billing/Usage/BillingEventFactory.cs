using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public static class BillingEventFactory
{
    public static BillingEventRecord FromUsageEvent(
        UsageEvent usageEvent,
        BillingCostBreakdown? costs = null,
        Guid? billingEventId = null)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        Guid? tenantId = Guid.TryParse(usageEvent.TenantId, out var parsedTenantId)
            ? parsedTenantId
            : null;
        Guid? apiKeyId = Guid.TryParse(usageEvent.ApiKeyId, out var parsedApiKeyId)
            ? parsedApiKeyId
            : null;

        return new BillingEventRecord(
            billingEventId ?? Guid.NewGuid(),
            usageEvent.RequestId,
            tenantId,
            apiKeyId,
            usageEvent.ModelId,
            usageEvent.CostCenter,
            usageEvent.PromptTokens,
            usageEvent.CompletionTokens,
            costs?.InputCost,
            costs?.OutputCost,
            costs?.TotalCost,
            usageEvent.DurationMs,
            usageEvent.TimestampUtc);
    }
}
