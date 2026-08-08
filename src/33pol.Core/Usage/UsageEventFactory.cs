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
    /// Both sides are approximated. Leaving prompt tokens at zero was itself a billing hole: the
    /// upstream had already read and charged for the whole prompt, and for retrieval-augmented or
    /// long-context workloads the prompt is the dominant cost — so a client that disconnected just
    /// before the terminal usage frame paid for a handful of output tokens and nothing for a
    /// hundred-thousand-token input. The prompt estimate is derived from the request body the
    /// gateway actually forwarded, which is an observation rather than a guess.
    /// </remarks>
    public static UsageEvent Estimated(
        string requestId,
        string modelId,
        long estimatedPromptTokens,
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
            PromptTokens = Math.Max(0, estimatedPromptTokens),
            CompletionTokens = Math.Max(0, estimatedCompletionTokens),
            TotalTokens = 0,
            TokenSource = UsageTokenSource.Estimated,
            DurationMs = durationMs,
            CostCenter = tenant?.CostCenter,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Approximates prompt tokens from the byte length of the forwarded request body.
    /// </summary>
    /// <remarks>
    /// Used only when authoritative usage never arrived. Four bytes per token is the conventional
    /// rule of thumb for English text under BPE tokenisers; the body also carries JSON scaffolding,
    /// so the result tends to run slightly high — the conservative direction for billing, and far
    /// closer than the zero it replaces. Events built this way are tagged
    /// <see cref="UsageTokenSource.Estimated"/> so reconciliation can exclude them.
    /// </remarks>
    public const int EstimatedBytesPerPromptToken = 4;

    public static long EstimatePromptTokens(long requestBodyBytes) =>
        requestBodyBytes <= 0
            ? 0
            : Math.Max(1, requestBodyBytes / EstimatedBytesPerPromptToken);

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
