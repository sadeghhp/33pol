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
    private int _enqueued;

    public InferenceUsageCapture(
        IUsageRecorder usageRecorder,
        IGatewayMetricsCollector metrics,
        string canonicalModelId,
        string requestId,
        DateTimeOffset startedUtc,
        TenantContext? tenant)
    {
        _usageRecorder = usageRecorder;
        _metrics = metrics;
        _canonicalModelId = canonicalModelId;
        _requestId = requestId;
        _startedUtc = startedUtc;
        _tenant = tenant;
    }

    public void CaptureFromJsonBody(ReadOnlySpan<byte> body) => Capture(UsageJsonParser.Parse(body));

    public void CaptureFromSseText(string sseText) => Capture(UsageJsonParser.ParseSseText(sseText));

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
        // arrived at all) is the only systematic over-count, so subtract at most one.
        var estimatedCompletionTokens = Math.Max(1, stats.FrameCount - 1);

        _metrics.RecordEstimatedUsage(_canonicalModelId);
        EnqueueUsage(ParsedUsage.None, estimatedCompletionTokens);
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
    /// True once a usage event has been handed to the recorder. The router uses this to decide
    /// whether downstream persistence will settle the request's budget reservation, or whether it
    /// must release the reservation itself.
    /// </summary>
    public bool HasEnqueuedUsage => Volatile.Read(ref _enqueued) != 0;

    /// <summary>
    /// Enqueues a usage event. When <paramref name="estimatedCompletionTokens"/> is supplied the
    /// event is marked <see cref="UsageTokenSource.Estimated"/> instead of carrying parsed counts.
    /// </summary>
    private void EnqueueUsage(ParsedUsage usage, long? estimatedCompletionTokens = null)
    {
        var durationMs = (DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds;

        var usageEvent = estimatedCompletionTokens is long estimated
            ? UsageEventFactory.Estimated(
                _requestId,
                _canonicalModelId,
                estimated,
                durationMs,
                _tenant)
            : UsageEventFactory.FromParsedUsage(
                _requestId,
                _canonicalModelId,
                usage,
                durationMs,
                _tenant);

        _usageRecorder.Enqueue(usageEvent);
        Volatile.Write(ref _enqueued, 1);
    }
}
