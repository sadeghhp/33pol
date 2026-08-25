using Pol33.Core.Abstractions;
using Pol33.Core.Models.Overview;
using Pol33.Observability.Policy;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Metrics;

public sealed class GatewayMetricsCollector(GatewayRuntimeState runtimeState, PolicyPressureTracker? policy = null) : IGatewayMetricsCollector, IUsageQualityCounters
{
    private long _parseFailures;
    private long _estimatedUsage;
    private long _unsplitUsage;
    private long _droppedEvents;

    public long ParseFailures => Interlocked.Read(ref _parseFailures);

    public long EstimatedUsage => Interlocked.Read(ref _estimatedUsage);

    public long UnsplitUsage => Interlocked.Read(ref _unsplitUsage);

    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public void RecordRateLimitRejection(string reason) => RecordRateLimitRejection(reason, tenantId: null, modelId: null);

    public void RecordRateLimitRejection(string reason, string? tenantId, string? modelId)
    {
        var kind = reason.Contains("stream", StringComparison.OrdinalIgnoreCase)
            ? RejectionReason.StreamConcurrency
            : RejectionReason.RateLimit;
        runtimeState.RecordRateLimitRejection(kind, modelId);
        policy?.RecordRejection(kind, tenantId, modelId);
        GatewayMeters.RateLimitRejections.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordQuotaRejection() => RecordQuotaRejection(tenantId: null, modelId: null);

    public void RecordQuotaRejection(string? tenantId, string? modelId)
    {
        runtimeState.RecordQuotaRejection(RejectionReason.Quota, modelId);
        policy?.RecordRejection(RejectionReason.Quota, tenantId, modelId);
        GatewayMeters.QuotaRejections.Add(1);
    }

    public void RecordBudgetRejection(string? tenantId, string? budgetName, string modelId)
    {
        // The request itself completes as a failed inference (the scope's outcome), so only the
        // reason breakdown and the quota-blocked counter see it — never a second request.
        runtimeState.RecordQuotaRejection(RejectionReason.Budget, modelId);
        policy?.RecordBudgetRejection(tenantId, budgetName, modelId);
    }

    public void RecordGrantDenial(string? tenantId, string modelId)
    {
        policy?.RecordGrantDenial(tenantId, modelId);
    }

    public void RecordModelResolve(string result, string? requestedModel)
    {
        if (result == "not_found")
        {
            runtimeState.RecordReasonOnly(RejectionReason.ModelNotFound, modelId: null);
            if (requestedModel is not null)
            {
                policy?.RecordUnknownModel(requestedModel);
            }
        }

        RecordModelResolve(result);
    }

    public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) =>
        GatewayTokenMetricsRecorder.Record(modelId, promptTokens, completionTokens);

    public void RecordUsageParseFailure(string modelId)
    {
        Interlocked.Increment(ref _parseFailures);
        GatewayMeters.UsageParseFailures.Add(1, new KeyValuePair<string, object?>("model", modelId));
    }

    public void RecordEstimatedUsage(string modelId)
    {
        Interlocked.Increment(ref _estimatedUsage);
        GatewayMeters.EstimatedUsage.Add(1, new KeyValuePair<string, object?>("model", modelId));
    }

    public void RecordUnsplitUsage(string modelId)
    {
        Interlocked.Increment(ref _unsplitUsage);
        GatewayMeters.UnsplitUsage.Add(1, new KeyValuePair<string, object?>("model", modelId));
    }

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

    public void RecordBulkheadQueuedChange(string modelId, int delta) =>
        GatewayMeters.BulkheadQueued.Add(
            delta,
            new KeyValuePair<string, object?>("model", modelId));

    public void RecordTimeToFirstToken(string modelId, double seconds)
    {
        runtimeState.RecordTimeToFirstToken(modelId, seconds * 1_000d);
        GatewayMeters.TimeToFirstToken.Record(
            seconds,
            new KeyValuePair<string, object?>("model", modelId));
    }

    /// <summary>
    /// Shares <c>gateway_usage_writer_dropped_total</c> with the channel-full path: either way the
    /// request never reaches the ledger, and the existing writer-backlog alert should fire.
    /// </summary>
    public void RecordUsageEventsDropped(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _droppedEvents, count);
            GatewayMeters.UsageWriterDropped.Add(count);
        }
    }

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
