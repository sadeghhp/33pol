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

    /// <summary>
    /// How long a half-open probe may go without reporting an outcome before its permit is
    /// reclaimed and handed to the next caller.
    /// </summary>
    /// <remarks>
    /// A half-open breaker admits one probe and refuses everyone else until that probe reports back.
    /// On an inference gateway the probe <em>is</em> a generation, which legitimately runs for
    /// minutes — so a breaker that tripped during a slow patch stayed shut for the probe's whole
    /// duration rather than for <see cref="BreakDuration"/>, and a merely slow model presented to
    /// every caller as a model that answered nothing at all. Reclaiming on a deadline bounds that:
    /// traffic resumes trickling through at worst one request per timeout even while the probe runs.
    /// </remarks>
    public TimeSpan HalfOpenProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static CircuitBreakerPolicyOptions FromGatewayResilience(GatewayResilienceOptions resilience) =>
        new()
        {
            FailureThreshold = resilience.CircuitBreakerFailureThreshold,
            BreakDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerBreakDurationSeconds),
            SamplingWindow = TimeSpan.FromSeconds(Math.Max(1, resilience.CircuitBreakerSamplingWindowSeconds)),
            FailureRatioThreshold = resilience.CircuitBreakerFailureRatioThreshold,
            HalfOpenProbeTimeout =
                TimeSpan.FromSeconds(Math.Max(1, resilience.CircuitBreakerHalfOpenProbeTimeoutSeconds)),
        };
}
