using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Publishes <c>gateway_circuit_breaker_state</c> (0 closed, 1 half_open, 2 open) per model.
/// </summary>
public sealed class GatewayCircuitBreakerMetricsExporter(ICircuitBreakerStateSource stateSource) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_circuit_breaker_state",
            ObserveMeasurements,
            description: "Circuit breaker state per model (0=closed, 1=half_open, 2=open)");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<Measurement<int>> ObserveMeasurements() =>
        ObserveMeasurements(stateSource);

    public static IEnumerable<Measurement<int>> ObserveMeasurements(ICircuitBreakerStateSource source)
    {
        foreach (var state in source.GetStates())
        {
            yield return new Measurement<int>(
                state.State,
                new KeyValuePair<string, object?>("model", state.ModelId));
        }
    }
}
