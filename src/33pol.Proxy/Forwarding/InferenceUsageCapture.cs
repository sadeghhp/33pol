using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Proxy.Forwarding;

public sealed class InferenceUsageCapture
{
    private readonly IUsageRecorder _usageRecorder;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly string _canonicalModelId;
    private readonly string _requestId;
    private readonly DateTimeOffset _startedUtc;
    private readonly TenantContext? _tenant;
    private readonly string? _quotaPartition;
    private readonly long _requestBodyBytes;
    private int _enqueued;
    private UsageEvent? _captured;

    public InferenceUsageCapture(
        IUsageRecorder usageRecorder,
        IGatewayMetricsCollector metrics,
        string canonicalModelId,
        string requestId,
        DateTimeOffset startedUtc,
        TenantContext? tenant,
        long requestBodyBytes = 0,
        string? quotaPartition = null)
    {
        _usageRecorder = usageRecorder;
        _metrics = metrics;
        _canonicalModelId = canonicalModelId;
        _requestId = requestId;
        _startedUtc = startedUtc;
        _tenant = tenant;
        _quotaPartition = quotaPartition;
        _requestBodyBytes = requestBodyBytes;
    }

    public void CaptureFromJsonBody(ReadOnlySpan<byte> body) =>
        CaptureFromJsonBody(body, ReadOnlySpan<byte>.Empty, new UsageCaptureStats(0, body.Length));

    public void CaptureFromSseText(string sseText) => Capture(UsageJsonParser.ParseSseText(sseText));

    /// <summary>
    /// Captures usage from a non-streaming JSON body, falling back to a fragment scan of the
    /// retained tail when the body outgrew the head buffer.
    /// </summary>
    /// <remarks>
    /// The gap this closes: any non-streaming response larger than
    /// <see cref="UsageCapturingStream.MaxHeadBytes"/> reached the parser truncated, threw, and
    /// recorded no usage — so it was never billed. Batch embeddings responses are routinely
    /// megabytes, which made that the normal case rather than an edge case. The <c>usage</c> object
    /// is at the end of an OpenAI-shaped body, so the retained tail always contains it.
    /// </remarks>
    internal void CaptureFromJsonBody(
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> tail,
        UsageCaptureStats stats)
    {
        // The head holds the entire body: parse it as a whole document, which is exact.
        if (!head.IsEmpty && stats.TotalBytes <= head.Length)
        {
            Capture(UsageJsonParser.Parse(head));
            return;
        }

        // Body outgrew the head cap (or none was retained). Recover the usage object from the tail.
        var parsed = UsageJsonParser.ParseUsageFragment(tail);
        if (!parsed.HasUsage && !head.IsEmpty)
        {
            // Unusual shape — usage near the start of an oversized body. Cheap to also try.
            parsed = UsageJsonParser.ParseUsageFragment(head);
        }

        Capture(parsed);
    }

    /// <summary>
    /// Captures usage from a streamed body, falling back to an estimate when the authoritative
    /// terminal usage frame never arrived but the upstream had already streamed content.
    /// </summary>
    /// <remarks>
    /// The gap this closes: a client that disconnects mid-stream produced tokens the upstream has
    /// already charged for, but the terminal usage frame is never received, so parsing failed and
    /// nothing was billed at all. Repeated deliberately, that is free inference. The estimate is
    /// recorded as <see cref="UsageTokenSource.Estimated"/> so it stays distinguishable from
    /// authoritative usage for reconciliation.
    /// </remarks>
    internal void CaptureFromSseText(string sseText, UsageCaptureStats stats)
    {
        var parsed = UsageJsonParser.ParseSseText(sseText);
        if (parsed.HasUsage)
        {
            Capture(parsed);
            return;
        }

        if (!stats.ProducedOutput)
        {
            // Nothing was streamed, so there is nothing to estimate from. Do not fabricate usage.
            _metrics.RecordUsageParseFailure(_canonicalModelId);
            return;
        }

        // Each SSE frame carries roughly one token of content; the trailing [DONE] frame (if it
        // arrived at all) is the only systematic over-count, so subtract at most one. The prompt
        // side is approximated from the request body that was actually forwarded — the upstream
        // read and charged for all of it regardless of how the response ended.
        var estimatedCompletionTokens = Math.Max(1, stats.FrameCount - 1);
        var estimatedPromptTokens = UsageEventFactory.EstimatePromptTokens(_requestBodyBytes);

        _metrics.RecordEstimatedUsage(_canonicalModelId);
        EnqueueUsage(ParsedUsage.None, estimatedPromptTokens, estimatedCompletionTokens);
    }

    /// <summary>
    /// Reports a failure of the capture machinery itself (as opposed to unparseable usage), so it is
    /// observable rather than silently swallowed.
    /// </summary>
    /// <remarks>
    /// Capture runs during response teardown, after the body has reached the client. Letting an
    /// exception escape would fault a request that already succeeded, so
    /// <see cref="UsageCapturingStream"/> contains it and routes it here.
    /// </remarks>
    public void OnCaptureFailed(Exception exception) =>
        _metrics.RecordUsageParseFailure(_canonicalModelId);

    private void Capture(ParsedUsage usage)
    {
        if (!usage.HasUsage)
        {
            _metrics.RecordUsageParseFailure(_canonicalModelId);
            return;
        }

        if (usage.Kind == UsageParseKind.TotalOnly)
        {
            // Recorded so operators can see which models are billed from an approximation rather
            // than a real input/output split.
            _metrics.RecordUnsplitUsage(_canonicalModelId);
        }

        EnqueueUsage(usage);
    }

    /// <summary>
    /// True once a usage event has been <em>accepted</em> by the recorder. The router uses this to
    /// decide whether downstream persistence will settle the request's budget reservation, or
    /// whether it must release the reservation itself.
    /// </summary>
    /// <remarks>
    /// This tracks acceptance, not the attempt. Setting it unconditionally meant a saturated usage
    /// queue — which silently drops — left the router believing persistence would settle the
    /// reservation, so it held budget for its whole TTL. Under sustained load those accumulated into
    /// phantom spend that hard-stopped tenants nowhere near their limit.
    /// </remarks>
    public bool HasEnqueuedUsage => Volatile.Read(ref _enqueued) != 0;

    /// <summary>
    /// The usage event built from the response, whether or not the recorder accepted it. The router
    /// copies its token counts onto the live-feed row at completion so the console shows them
    /// immediately, without waiting for the billing writer's flush.
    /// </summary>
    public UsageEvent? CapturedUsage => Volatile.Read(ref _captured);

    /// <summary>
    /// Enqueues a usage event. When <paramref name="estimatedCompletionTokens"/> is supplied the
    /// event is marked <see cref="UsageTokenSource.Estimated"/> instead of carrying parsed counts.
    /// </summary>
    private void EnqueueUsage(
        ParsedUsage usage,
        long? estimatedPromptTokens = null,
        long? estimatedCompletionTokens = null)
    {
        var durationMs = (DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds;

        var usageEvent = estimatedCompletionTokens is long estimatedCompletion
            ? UsageEventFactory.Estimated(
                _requestId,
                _canonicalModelId,
                estimatedPromptTokens ?? 0,
                estimatedCompletion,
                durationMs,
                _tenant)
            : UsageEventFactory.FromParsedUsage(
                _requestId,
                _canonicalModelId,
                usage,
                durationMs,
                _tenant);

        usageEvent = UsageEventFactory.WithQuotaPartition(usageEvent, _quotaPartition);
        Volatile.Write(ref _captured, usageEvent);

        if (_usageRecorder.Enqueue(usageEvent))
        {
            Volatile.Write(ref _enqueued, 1);
        }
    }
}
