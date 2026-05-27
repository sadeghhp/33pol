using Pol33.Core.Abstractions;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Metrics;

public sealed class GatewayMetricsCollector(GatewayRuntimeState runtimeState) : IGatewayMetricsCollector
{
    public void RecordRateLimitRejection(string reason)
    {
        runtimeState.RecordRateLimitRejection();
        GatewayMeters.RateLimitRejections.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordQuotaRejection()
    {
        runtimeState.RecordQuotaRejection();
        GatewayMeters.QuotaRejections.Add(1);
    }

    public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) =>
        GatewayTokenMetricsRecorder.Record(modelId, promptTokens, completionTokens);

    public void RecordUsageParseFailure(string modelId) =>
        GatewayMeters.UsageParseFailures.Add(1, new KeyValuePair<string, object?>("model", modelId));

    public void RecordInferenceRouted(string modelId, string route, bool isStreaming) =>
        GatewayMeters.InferenceRoute.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("route", route),
            new KeyValuePair<string, object?>("stream", isStreaming ? "true" : "false"));

    public void RecordForwardAttempt(string modelId, string outcome) =>
        GatewayMeters.ForwardAttempts.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordModelResolve(string result) =>
        GatewayMeters.ModelResolve.Add(1, new KeyValuePair<string, object?>("result", result));
}
