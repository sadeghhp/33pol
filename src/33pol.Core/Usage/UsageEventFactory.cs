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

    /// <summary>
    /// Builds a usage event from a parsed <c>usage</c> object, preserving whether the upstream
    /// reported a split or only a combined total so pricing can treat them differently.
    /// </summary>
    public static UsageEvent FromParsedUsage(
        string requestId,
        string modelId,
        ParsedUsage usage,
        double durationMs,
        TenantContext? tenant = null,
        DateTimeOffset? timestampUtc = null) =>
        new()
        {
            RequestId = requestId,
            TenantId = tenant?.TenantId,
            ApiKeyId = tenant?.ApiKeyId,
            ModelId = modelId,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.Kind == UsageParseKind.TotalOnly ? usage.TotalTokens : 0,
            TokenSource = usage.Kind == UsageParseKind.TotalOnly
                ? UsageTokenSource.TotalOnly
                : UsageTokenSource.Split,
            DurationMs = durationMs,
            CostCenter = tenant?.CostCenter,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Builds a usage event whose completion tokens are an approximation, because the authoritative
    /// usage never arrived (typically a client disconnect mid-stream).
    /// </summary>
    /// <remarks>
    /// Prompt tokens are left at zero: the request body was not counted and guessing it would be
    /// fabrication. Only what the gateway actually observed being streamed is recorded.
    /// </remarks>
    public static UsageEvent Estimated(
        string requestId,
        string modelId,
        long estimatedCompletionTokens,
        double durationMs,
        TenantContext? tenant = null,
        DateTimeOffset? timestampUtc = null) =>
        new()
        {
            RequestId = requestId,
            TenantId = tenant?.TenantId,
            ApiKeyId = tenant?.ApiKeyId,
            ModelId = modelId,
            PromptTokens = 0,
            CompletionTokens = Math.Max(0, estimatedCompletionTokens),
            TotalTokens = 0,
            TokenSource = UsageTokenSource.Estimated,
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
            TotalTokens = usageEvent.TotalTokens,
            TokenSource = usageEvent.TokenSource,
            DurationMs = usageEvent.DurationMs,
            CostCenter = usageEvent.CostCenter ?? tenant?.CostCenter,
            TimestampUtc = usageEvent.TimestampUtc,
        };
}
