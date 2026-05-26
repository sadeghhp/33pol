using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

internal static class BillingEventMapper
{
    public static BillingEventRecord FromUsageEvent(UsageEvent usageEvent) =>
        new(
            Guid.NewGuid(),
            usageEvent.RequestId,
            ParseGuid(usageEvent.TenantId),
            ParseGuid(usageEvent.ApiKeyId),
            usageEvent.ModelId,
            usageEvent.CostCenter,
            usageEvent.PromptTokens,
            usageEvent.CompletionTokens,
            InputCost: null,
            OutputCost: null,
            TotalCost: null,
            usageEvent.DurationMs,
            usageEvent.TimestampUtc == default ? DateTimeOffset.UtcNow : usageEvent.TimestampUtc);

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var id) ? id : null;
}
