using Pol33.Core.Configuration;

namespace Pol33.Policy.CircuitBreaker;

public sealed class CircuitBreakerPolicyOptions
{
    /// <summary>
    /// Failures within <see cref="SamplingWindow"/> that trip the breaker, provided the failure
    /// ratio also reaches <see cref="FailureRatioThreshold"/>.
    /// </summary>
    public int FailureThreshold { get; init; } = 5;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far back outcomes are counted.
    /// </summary>
    /// <remarks>
    /// The breaker used to require <em>consecutive</em> failures, resetting its counter on any
    /// success. A backend failing half its requests therefore never tripped, because a success
    /// always arrived before the threshold was reached — so the main protection against a degraded
    /// upstream did nothing in the most common degradation mode and only caught hard-down backends.
    /// </remarks>
    public TimeSpan SamplingWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fraction of failed outcomes in the window required to trip, so a busy healthy backend with a
    /// steady trickle of unrelated errors is not opened by absolute count alone.
    /// </summary>
    public double FailureRatioThreshold { get; init; } = 0.5;

    public static CircuitBreakerPolicyOptions FromGatewayResilience(GatewayResilienceOptions resilience) =>
        new()
        {
            FailureThreshold = resilience.CircuitBreakerFailureThreshold,
            BreakDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerBreakDurationSeconds),
            SamplingWindow = TimeSpan.FromSeconds(Math.Max(1, resilience.CircuitBreakerSamplingWindowSeconds)),
            FailureRatioThreshold = resilience.CircuitBreakerFailureRatioThreshold,
        };
}
