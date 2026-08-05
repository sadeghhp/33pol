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
/// Maximum gap between two chunks of a streaming response body, reset on every forwarded chunk. A
/// breach means the upstream stalled after the response had already started, which is inconclusive
/// about backend health.
/// </param>
/// <remarks>
/// Keeping these separate is what allows a healthy multi-minute generation to complete: a single
/// total-duration deadline truncated such streams and attributed the truncation to the backend,
/// tripping the circuit breaker on models that were working correctly.
/// </remarks>
public readonly record struct InferenceForwardTimeouts(TimeSpan HeaderTimeout, TimeSpan StreamIdleTimeout)
{
    public static InferenceForwardTimeouts FromResilience(GatewayResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(resilience);

        return new InferenceForwardTimeouts(
            TimeSpan.FromSeconds(Math.Max(1, resilience.ForwardTimeoutSeconds)),
            TimeSpan.FromSeconds(Math.Max(1, resilience.StreamIdleTimeoutSeconds)));
    }
}
