namespace Pol33.Core.Configuration;

public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>
    /// How long the gateway waits for the upstream to return response <em>headers</em>. A breach is
    /// a genuine backend-health signal and counts toward the circuit breaker.
    ///
    /// For non-streaming requests the whole response arrives with the headers, so this remains the
    /// total request budget. For streaming requests the body is governed by
    /// <see cref="StreamIdleTimeoutSeconds"/> instead: applying a total-duration cap to a stream
    /// truncated long-but-healthy generations and recorded them as backend failures, which opened
    /// the breaker on models that were working correctly.
    /// </summary>
    public int ForwardTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum gap between two chunks of a streaming response body before the gateway gives up.
    /// The clock resets on every chunk forwarded to the client, so a stream of any total duration
    /// survives as long as the upstream keeps producing.
    ///
    /// A breach means the upstream stalled mid-stream, which says nothing conclusive about backend
    /// health (the response already started), so it abandons the circuit-breaker probe rather than
    /// recording a failure.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 120;

    public long MaxRequestBodyBytes { get; set; } = 26_214_400;

    public int MaxConcurrentForwardsPerModel { get; set; } = 64;

    public int MaxTrackedResilienceModels { get; set; } = 1024;

    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;
}
