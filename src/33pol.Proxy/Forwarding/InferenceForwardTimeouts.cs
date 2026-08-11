using Pol33.Core.Configuration;

namespace Pol33.Proxy.Forwarding;

/// <summary>
/// The two independent deadlines a forwarded inference request is subject to.
/// </summary>
/// <param name="HeaderTimeout">
/// How long to wait for upstream response headers. A breach means the backend never answered and is
/// a genuine health signal.
/// </param>
/// <param name="StreamIdleTimeout">
/// Maximum gap between two chunks of the response body, reset on every forwarded chunk. A breach
/// means the upstream stalled after the response had already started, which is inconclusive about
/// backend health.
/// </param>
/// <remarks>
/// Keeping these separate is what allows a healthy multi-minute generation to complete: a single
/// total-duration deadline truncated such responses and attributed the truncation to the backend,
/// tripping the circuit breaker on models that were working correctly.
/// </remarks>
public readonly record struct InferenceForwardTimeouts(TimeSpan HeaderTimeout, TimeSpan StreamIdleTimeout)
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>Per-megabyte header allowance, kept alongside the base so it can be applied per request.</summary>
    public TimeSpan HeaderTimeoutPerRequestMegabyte { get; init; }

    /// <summary>
    /// Ceiling for the scaled header allowance. Finite by default so the arithmetic below can never
    /// overflow, however large a body an operator permits.
    /// </summary>
    public TimeSpan MaxHeaderTimeout { get; init; } = TimeSpan.FromDays(1);

    public static InferenceForwardTimeouts FromResilience(GatewayResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(resilience);

        var baseHeaderTimeout = TimeSpan.FromSeconds(Math.Max(1, resilience.ForwardTimeoutSeconds));
        var maxHeaderTimeout = TimeSpan.FromSeconds(
            Math.Max(resilience.MaxForwardTimeoutSeconds, resilience.ForwardTimeoutSeconds));

        return new InferenceForwardTimeouts(
            baseHeaderTimeout,
            TimeSpan.FromSeconds(Math.Max(1, resilience.StreamIdleTimeoutSeconds)))
        {
            HeaderTimeoutPerRequestMegabyte =
                TimeSpan.FromSeconds(Math.Max(0, resilience.ForwardTimeoutSecondsPerRequestMegabyte)),
            MaxHeaderTimeout = maxHeaderTimeout,
        };
    }

    /// <summary>
    /// Widens the header allowance in proportion to the request body being forwarded, capped at
    /// <see cref="MaxHeaderTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Time to first response byte grows with the prompt, because the backend reads and pre-fills the
    /// whole context before it can answer. Charging a long-context request the same allowance as a
    /// one-line one is what made a working backend look dead to the circuit breaker.
    /// </remarks>
    public InferenceForwardTimeouts ForRequestBody(long requestBodyBytes)
    {
        if (requestBodyBytes <= 0 ||
            HeaderTimeoutPerRequestMegabyte <= TimeSpan.Zero ||
            MaxHeaderTimeout <= HeaderTimeout)
        {
            return this;
        }

        // Rounded up, so any body at all buys at least one megabyte's worth of extra allowance.
        // Divided before the round-up rather than after: adding to the byte count first overflows on
        // a body near long.MaxValue, which flipped the allowance negative.
        var megabytes = (requestBodyBytes / BytesPerMegabyte)
            + (requestBodyBytes % BytesPerMegabyte == 0 ? 0 : 1);

        var allowanceSeconds = Math.Clamp(
            HeaderTimeout.TotalSeconds + (HeaderTimeoutPerRequestMegabyte.TotalSeconds * megabytes),
            HeaderTimeout.TotalSeconds,
            MaxHeaderTimeout.TotalSeconds);

        return this with { HeaderTimeout = TimeSpan.FromSeconds(allowanceSeconds) };
    }
}
