using Pol33.Core.Identity;
using Pol33.Core.Models;

namespace Pol33.Core.Usage;

public static class UsageEventFactory
{
    public static UsageEvent FromInference(
        string requestId,
        string modelId,
        long promptTokens,
        long completionTokens,
        double durationMs,
        TenantContext? tenant = null,
        DateTimeOffset? timestampUtc = null) =>
        new()
        {
            RequestId = requestId,
            TenantId = tenant?.TenantId,
            ApiKeyId = tenant?.ApiKeyId,
            ModelId = modelId,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = durationMs,
            CostCenter = tenant?.CostCenter,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
        };

    public static UsageEvent WithCostCenter(UsageEvent usageEvent, TenantContext? tenant) =>
        new()
        {
            RequestId = usageEvent.RequestId,
            TenantId = usageEvent.TenantId ?? tenant?.TenantId,
            ApiKeyId = usageEvent.ApiKeyId ?? tenant?.ApiKeyId,
            ModelId = usageEvent.ModelId,
            PromptTokens = usageEvent.PromptTokens,
            CompletionTokens = usageEvent.CompletionTokens,
            DurationMs = usageEvent.DurationMs,
            CostCenter = usageEvent.CostCenter ?? tenant?.CostCenter,
            TimestampUtc = usageEvent.TimestampUtc,
        };
}
