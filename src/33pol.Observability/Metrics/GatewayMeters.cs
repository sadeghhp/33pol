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
}
