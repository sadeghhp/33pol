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
                .AddMeter(GatewayMeters.MeterName)
                .AddPrometheusExporter());

        return services;
    }
}
