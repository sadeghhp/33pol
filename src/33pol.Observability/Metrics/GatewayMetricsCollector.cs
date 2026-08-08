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

    public void RecordEstimatedUsage(string modelId) =>
        GatewayMeters.EstimatedUsage.Add(1, new KeyValuePair<string, object?>("model", modelId));

    public void RecordUnsplitUsage(string modelId) =>
        GatewayMeters.UnsplitUsage.Add(1, new KeyValuePair<string, object?>("model", modelId));

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

    public void RecordCircuitBreakerTransition(string modelId, string toState) =>
        GatewayMeters.CircuitBreakerTransitions.Add(
            1,
            new KeyValuePair<string, object?>("model", modelId),
            new KeyValuePair<string, object?>("to_state", toState));

    public void RecordBulkheadRejection(string modelId) =>
        GatewayMeters.BulkheadRejections.Add(1, new KeyValuePair<string, object?>("model", modelId));

    public void RecordBulkheadInflightChange(string modelId, int delta) =>
        GatewayMeters.BulkheadInflight.Add(
            delta,
            new KeyValuePair<string, object?>("model", modelId));

    public void RecordTimeToFirstToken(string modelId, double seconds) =>
        GatewayMeters.TimeToFirstToken.Record(
            seconds,
            new KeyValuePair<string, object?>("model", modelId));

    public void RecordBillingReconciliation(int discrepancyCount, double absoluteCostDrift)
    {
        // Recorded as gauges, not counters: the question is "is billing consistent right now", and a
        // monotonic counter cannot answer that — it never returns to zero once a transient
        // discrepancy is resolved. Every completed sweep overwrites both, so a stale value means the
        // sweep itself stopped, which is why the job also emits a heartbeat.
        GatewayMeters.SetBillingReconciliation(discrepancyCount, absoluteCostDrift);
        GatewayMeters.BillingReconciliationRuns.Add(1);
    }
}
