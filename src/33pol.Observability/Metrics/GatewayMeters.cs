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
}
