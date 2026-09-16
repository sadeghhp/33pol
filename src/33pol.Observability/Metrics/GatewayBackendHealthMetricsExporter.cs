using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Publishes <c>gateway_backend_health</c> (1 healthy, 0 unhealthy) per registered model, and
/// <c>gateway_models_configured</c> — the number of routes the gateway is willing to serve.
/// </summary>
/// <remarks>
/// The per-model gauge alone cannot express "this gateway can serve nothing". With an empty registry
/// it emits no series at all, so <c>max(gateway_backend_health) == 0</c> — the outage alert — has
/// nothing to evaluate and silently never fires. A gateway that has lost or never had its routes is
/// exactly the case that most needs to alert, so the count is exported unconditionally: it is always
/// present, and it is 0 precisely when there is nothing to serve.
/// </remarks>
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

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_models_configured",
            () => ObserveConfiguredModels(registry),
            description: "Routes the gateway is configured to serve (0 = nothing configured)");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Unlabelled and single-valued, so it exists even with an empty registry — the whole point.
    /// Stopped routes are excluded: a route an operator took out of service is not something the
    /// gateway is willing to serve, and counting it would hide a registry stopped down to nothing.
    /// </summary>
    public static IEnumerable<Measurement<int>> ObserveConfiguredModels(IModelRegistry registry) =>
        [new Measurement<int>(registry.GetAllModels().Count(model => model.IsServing()))];

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
