using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Publishes <c>gateway_backend_health</c> (1 healthy, 0 unhealthy) per registered model.
/// </summary>
public sealed class GatewayBackendHealthMetricsExporter(
    IModelRegistry registry,
    IBackendHealthStore healthStore) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_backend_health",
            () => ObserveMeasurements(registry, healthStore),
            description: "Backend health per model (1 = healthy, 0 = unhealthy)");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static IEnumerable<Measurement<int>> ObserveMeasurements(
        IModelRegistry registry,
        IBackendHealthStore healthStore)
    {
        foreach (var model in registry.GetAllModels())
        {
            var value = healthStore.IsBackendHealthy(model.Id) ? 1 : 0;
            yield return new Measurement<int>(
                value,
                new KeyValuePair<string, object?>("model", model.Id));
        }
    }
}
