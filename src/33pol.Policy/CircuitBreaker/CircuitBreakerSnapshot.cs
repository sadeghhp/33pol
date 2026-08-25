namespace Pol33.Policy.CircuitBreaker;

/// <param name="OpenedAt">When the breaker last tripped; null while it is closed.</param>
/// <param name="FailuresInWindow">Failed outcomes inside the sampling window (0 while open — the window is cleared on trip).</param>
/// <param name="OutcomesInWindow">All outcomes inside the sampling window.</param>
/// <param name="RemainingBreak">Time until an open breaker lets a probe through; null unless open.</param>
public readonly record struct CircuitBreakerSnapshot(
    CircuitState State,
    DateTimeOffset? OpenedAt,
    int FailuresInWindow,
    int OutcomesInWindow,
    TimeSpan? RemainingBreak);
