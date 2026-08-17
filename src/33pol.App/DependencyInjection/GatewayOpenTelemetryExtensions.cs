using OpenTelemetry.Metrics;
using Pol33.Observability.Metrics;

namespace Pol33.App.DependencyInjection;

public static class GatewayOpenTelemetryExtensions
{
    public static IServiceCollection AddGatewayOpenTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // Managed heap, GC and thread-pool counters. The ASP.NET Core and HttpClient
                // instrumentations above cover request and dependency traffic but say nothing about
                // memory, which leaves the gateway's dominant failure mode unobservable: a large
                // request body is buffered, scanned and forwarded, so heap pressure — not request
                // rate — is what decides whether the process stays inside its container limit. Every
                // symptom of getting that wrong (OOMKill, a GC-bound tail latency, LOH growth under
                // long-context traffic) is invisible without these series.
                .AddRuntimeInstrumentation()
                .AddMeter(GatewayMeters.MeterName)
                // Seconds-shaped buckets for the two latency histograms. The SDK default boundaries
                // (0, 5, 10, 25 … 10000) are millisecond-shaped, so every inference between half a
                // second and a minute fell into the first two buckets and histogram_quantile() could
                // not tell a 1 s p95 from a 20 s one — the very question "is the model slow or is
                // the gateway queueing?" that this dashboard exists to answer.
                .AddView(
                    "gateway_inference_duration_seconds",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300, 600],
                    })
                .AddView(
                    "gateway_time_to_first_token_seconds",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30, 60],
                    })
                .AddPrometheusExporter());

        return services;
    }
}
