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

    /// <summary>
    /// How long the gateway keeps serving after it starts draining, so load balancers have time to
    /// observe the readiness probe flip and stop routing to this instance.
    /// </summary>
    /// <remarks>
    /// <para>Should be a small multiple of the readiness probe interval, and the host's shutdown
    /// timeout must exceed it or the drain is cut short.</para>
    ///
    /// <para>Defaults to 0 — stop immediately — because a nonzero value delays <em>every</em>
    /// shutdown, including local runs and test hosts, and only load-balanced deployments benefit.
    /// Set it wherever a load balancer or Kubernetes service fronts the gateway: without it the
    /// readiness probe flips at the same instant Kestrel stops accepting, so the balancer keeps
    /// routing to an instance that is already tearing down and every rolling restart drops requests.
    /// The Helm chart sets it.</para>
    /// </remarks>
    public int ShutdownDrainSeconds { get; set; }

    public int MaxConcurrentForwardsPerModel { get; set; } = 64;

    public int MaxTrackedResilienceModels { get; set; } = 1024;

    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;

    /// <summary>
    /// How far back the breaker counts outcomes when deciding whether a backend is failing.
    /// </summary>
    /// <remarks>
    /// Outcomes are counted over this rolling window rather than requiring an unbroken run of
    /// failures. A backend failing intermittently — the usual way an overloaded model server
    /// degrades — never produced a long enough consecutive run to trip the old counter, so the
    /// breaker only ever caught backends that were completely down.
    /// </remarks>
    public int CircuitBreakerSamplingWindowSeconds { get; set; } = 30;

    /// <summary>
    /// Fraction of outcomes in the window that must be failures before the breaker opens, applied in
    /// addition to <see cref="CircuitBreakerFailureThreshold"/>. Guards a high-throughput backend
    /// against being opened by an absolute count that it reaches while still mostly succeeding.
    /// </summary>
    public double CircuitBreakerFailureRatioThreshold { get; set; } = 0.5;
}
