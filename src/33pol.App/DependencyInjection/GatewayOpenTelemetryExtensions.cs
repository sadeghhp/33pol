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
                .AddPrometheusExporter());

        return services;
    }
}
