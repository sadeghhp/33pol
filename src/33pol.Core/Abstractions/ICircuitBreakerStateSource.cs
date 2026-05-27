namespace Pol33.Core.Abstractions;

/// <summary>
/// Snapshot of per-model circuit breaker state for Prometheus observable gauges.
/// State values: 0 = closed, 1 = half_open, 2 = open.
/// </summary>
public interface ICircuitBreakerStateSource
{
    IReadOnlyList<CircuitBreakerModelState> GetStates();
}

public sealed record CircuitBreakerModelState(string ModelId, int State);
