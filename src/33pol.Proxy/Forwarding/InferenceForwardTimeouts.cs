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

    /// <summary>
    /// Longest a single write of response-body bytes to the client may take. Distinct from
    /// <see cref="StreamIdleTimeout"/> on purpose: one bounds the upstream's silence, the other the
    /// client's refusal to read, and conflating them reported a slow client as an upstream stall.
    /// </summary>
    /// <remarks>
    /// Zero means "use <see cref="StreamIdleTimeout"/>", so a <see cref="InferenceForwardTimeouts"/>
    /// built from the two positional deadlines alone is still bounded on the write side.
    /// </remarks>
    public TimeSpan DownstreamWriteTimeout { get; init; }

    /// <summary>The write bound actually applied, resolving the zero default.</summary>
    public TimeSpan EffectiveDownstreamWriteTimeout =>
        DownstreamWriteTimeout > TimeSpan.Zero ? DownstreamWriteTimeout : StreamIdleTimeout;

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
            DownstreamWriteTimeout =
                TimeSpan.FromSeconds(Math.Max(1, resilience.DownstreamWriteTimeoutSeconds)),
        };
    }

    /// <summary>
    /// Allowance for the first byte of the response body, given how long the header phase already
    /// took.
    /// </summary>
    /// <remarks>
    /// <para>An SSE upstream (vLLM, SGLang, TGI) writes its response headers the moment it accepts
    /// the request — before the request is scheduled, before prefill, before any token exists. For
    /// a streaming request the header allowance is therefore consumed in milliseconds and the wait
    /// for the first token used to be governed by <see cref="StreamIdleTimeout"/> alone: a hard
    /// 120 s time-to-first-token ceiling on every streaming request, however large its prompt, and
    /// however carefully <see cref="ForRequestBody"/> had widened the header allowance for it.</para>
    ///
    /// <para>The first-byte allowance is the <em>remainder</em> of the header allowance — what the
    /// header phase did not use — and never less than the idle gap. Time to first byte is thereby
    /// bounded by the prompt-scaled header allowance in both response modes, which is what the
    /// allowance was sized for. Once a byte has arrived the idle gap applies as before.</para>
    /// </remarks>
    public TimeSpan FirstByteTimeout(TimeSpan headerPhaseElapsed)
    {
        if (headerPhaseElapsed < TimeSpan.Zero)
        {
            headerPhaseElapsed = TimeSpan.Zero;
        }

        var remaining = HeaderTimeout - headerPhaseElapsed;
        return remaining > StreamIdleTimeout ? remaining : StreamIdleTimeout;
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
