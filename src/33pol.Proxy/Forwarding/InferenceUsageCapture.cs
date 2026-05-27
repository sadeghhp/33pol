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

    public void CaptureFromJsonBody(ReadOnlySpan<byte> body)
    {
        if (!UsageJsonParser.TryParseUsage(body, out var promptTokens, out var completionTokens))
        {
            _metrics.RecordUsageParseFailure(_canonicalModelId);
            return;
        }

        EnqueueUsage(promptTokens, completionTokens);
    }

    public void CaptureFromSseText(string sseText)
    {
        if (!UsageJsonParser.TryParseUsageFromSseText(sseText, out var promptTokens, out var completionTokens))
        {
            _metrics.RecordUsageParseFailure(_canonicalModelId);
            return;
        }

        EnqueueUsage(promptTokens, completionTokens);
    }

    private void EnqueueUsage(long promptTokens, long completionTokens)
    {
        var durationMs = (DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds;
        var usageEvent = UsageEventFactory.FromInference(
            _requestId,
            _canonicalModelId,
            promptTokens,
            completionTokens,
            durationMs,
            _tenant);

        _usageRecorder.Enqueue(usageEvent);
    }
}
