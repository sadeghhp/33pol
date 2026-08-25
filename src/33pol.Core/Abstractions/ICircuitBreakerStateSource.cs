namespace Pol33.Core.Abstractions;

/// <summary>
/// Snapshot of per-model circuit breaker state for Prometheus observable gauges and the admin
/// Overview. State values: 0 = closed, 1 = half_open, 2 = open.
/// </summary>
public interface ICircuitBreakerStateSource
{
    IReadOnlyList<CircuitBreakerModelState> GetStates();
}

/// <param name="OpenedAt">When the breaker last tripped; null while closed.</param>
/// <param name="FailuresInWindow">Failures inside the sampling window (a closed breaker creeping toward its threshold).</param>
/// <param name="OutcomesInWindow">All outcomes inside the sampling window.</param>
/// <param name="LastTransitionUtc">When the state last changed; null if it never has.</param>
public sealed record CircuitBreakerModelState(
    string ModelId,
    int State,
    DateTimeOffset? OpenedAt = null,
    int FailuresInWindow = 0,
    int OutcomesInWindow = 0,
    DateTimeOffset? LastTransitionUtc = null);
