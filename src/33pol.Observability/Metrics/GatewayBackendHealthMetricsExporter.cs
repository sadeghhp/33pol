using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Observability.Metrics;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Publishes <c>gateway_backend_health</c> (1 healthy, 0 unhealthy) per registered model.
/// </summary>
public sealed class GatewayBackendHealthMetricsExporter(
    IModelRegistry registry,
    IBackendHealthStore healthStore) : IHostedService
{
    private IDisposable? _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = GatewayMeters.Meter.CreateObservableGauge(
            "gateway_backend_health",
            Observe,
            description: "Backend health per model (1 = healthy, 0 = unhealthy)");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<int>> Observe()
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
