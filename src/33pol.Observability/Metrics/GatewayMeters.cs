using System.Diagnostics.Metrics;

namespace Pol33.Observability.Metrics;

public static class GatewayMeters
{
    public const string MeterName = "Pol33.Gateway";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> InferenceRequests =
        Meter.CreateCounter<long>("gateway_inference_requests_total");

    public static readonly Counter<long> InferenceErrors =
        Meter.CreateCounter<long>("gateway_inference_errors_total");

    public static readonly Histogram<double> InferenceDuration =
        Meter.CreateHistogram<double>("gateway_inference_duration_seconds");

    public static readonly Histogram<double> TimeToFirstToken =
        Meter.CreateHistogram<double>("gateway_time_to_first_token_seconds");

    public static readonly UpDownCounter<long> ActiveStreams =
        Meter.CreateUpDownCounter<long>("gateway_active_streams");

    /// <summary>Inference requests being forwarded right now — streaming and non-streaming alike.</summary>
    public static readonly UpDownCounter<long> ActiveRequests =
        Meter.CreateUpDownCounter<long>("gateway_active_requests");

    public static readonly Counter<long> RateLimitRejections =
        Meter.CreateCounter<long>("gateway_rate_limit_rejections_total");

    public static readonly Counter<long> QuotaRejections =
        Meter.CreateCounter<long>("gateway_quota_rejections_total");

    public static readonly Counter<long> TokensTotal =
        Meter.CreateCounter<long>("gateway_tokens_total");

    public static readonly Counter<long> UsageParseFailures =
        Meter.CreateCounter<long>("gateway_usage_parse_failures_total");

    /// <summary>
    /// Responses whose upstream reported only a combined token total. Their cost is an approximation
    /// (priced at the dearer rate), so a persistently non-zero value for a model is a signal to check
    /// that upstream's usage reporting.
    /// </summary>
    public static readonly Counter<long> UnsplitUsage =
        Meter.CreateCounter<long>("gateway_usage_unsplit_total");

    /// <summary>
    /// Responses billed from a streamed-frame estimate rather than authoritative usage. A rising
    /// value for one tenant may indicate deliberate disconnect-before-completion.
    /// </summary>
    public static readonly Counter<long> EstimatedUsage =
        Meter.CreateCounter<long>("gateway_usage_estimated_total");

    public static readonly Counter<long> InferenceRoute =
        Meter.CreateCounter<long>("gateway_inference_route_total");

    public static readonly Counter<long> ForwardAttempts =
        Meter.CreateCounter<long>("gateway_forward_attempts_total");

    public static readonly Counter<long> ModelResolve =
        Meter.CreateCounter<long>("gateway_model_resolve_total");

    public static readonly Counter<long> CircuitBreakerTransitions =
        Meter.CreateCounter<long>("gateway_circuit_breaker_transitions_total");

    public static readonly Counter<long> BulkheadRejections =
        Meter.CreateCounter<long>("gateway_bulkhead_rejections_total");

    public static readonly UpDownCounter<long> BulkheadInflight =
        Meter.CreateUpDownCounter<long>("gateway_bulkhead_inflight");

    public static readonly UpDownCounter<long> UsageWriterQueueDepth =
        Meter.CreateUpDownCounter<long>("gateway_usage_writer_queue_depth");

    public static readonly Counter<long> UsageWriterDropped =
        Meter.CreateCounter<long>("gateway_usage_writer_dropped_total");

    /// <summary>Completed reconciliation sweeps. A flat line means the job has stopped running.</summary>
    public static readonly Counter<long> BillingReconciliationRuns =
        Meter.CreateCounter<long>("gateway_billing_reconciliation_runs_total");

    private static int _reconciliationDiscrepancies;
    private static double _reconciliationCostDrift;

    /// <summary>
    /// Rollup buckets whose totals disagree with the billing events behind them, as of the last
    /// sweep. <b>Alert on any non-zero value.</b>
    /// </summary>
    public static readonly ObservableGauge<int> BillingReconciliationDiscrepancies =
        Meter.CreateObservableGauge(
            "gateway_billing_reconciliation_discrepancies",
            static () => Volatile.Read(ref _reconciliationDiscrepancies));

    /// <summary>
    /// Total absolute money difference across those buckets, in the configured default currency.
    /// Reported alongside the count because one bucket out by a large amount and many out by
    /// rounding are very different incidents.
    /// </summary>
    public static readonly ObservableGauge<double> BillingReconciliationCostDrift =
        Meter.CreateObservableGauge(
            "gateway_billing_reconciliation_cost_drift",
            static () => Volatile.Read(ref _reconciliationCostDrift));

    internal static void SetBillingReconciliation(int discrepancyCount, double absoluteCostDrift)
    {
        Volatile.Write(ref _reconciliationDiscrepancies, discrepancyCount);
        Volatile.Write(ref _reconciliationCostDrift, absoluteCostDrift);
    }
}
