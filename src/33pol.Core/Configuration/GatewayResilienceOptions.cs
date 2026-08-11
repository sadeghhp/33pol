namespace Pol33.Core.Configuration;

public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>
    /// Base allowance for the upstream to return response <em>headers</em>. A breach means the
    /// backend never answered at all, which is a genuine health signal and counts toward the circuit
    /// breaker.
    ///
    /// The response body is governed by <see cref="StreamIdleTimeoutSeconds"/> in both streaming and
    /// non-streaming mode, and the allowance itself is widened per
    /// <see cref="ForwardTimeoutSecondsPerRequestMegabyte"/>. Applying a single total-duration cap
    /// truncated long-but-healthy generations and recorded them as backend failures, which opened
    /// the breaker on models that were working correctly.
    /// </summary>
    public int ForwardTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Extra header allowance granted per megabyte of request body actually forwarded.
    /// </summary>
    /// <remarks>
    /// <para>Time to first response byte scales with the prompt, because the backend must read and
    /// pre-fill the whole context before it can answer. A fixed allowance therefore expires on
    /// long-context requests purely because they are long, and the breaker counted each expiry
    /// against a backend that was working correctly — enough concurrent large-context requests took
    /// the model out of service for every caller.</para>
    ///
    /// <para>At the default, the 25 MB body cap buys 25 further minutes on top of
    /// <see cref="ForwardTimeoutSeconds"/>, while a small request is unaffected. Set to 0 to restore
    /// a flat allowance.</para>
    /// </remarks>
    public int ForwardTimeoutSecondsPerRequestMegabyte { get; set; } = 60;

    /// <summary>
    /// Ceiling on the scaled header allowance, so an oversized body can never grant an effectively
    /// unbounded deadline.
    /// </summary>
    public int MaxForwardTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum gap between two chunks of a response body before the gateway gives up. The clock
    /// resets on every chunk forwarded to the client, so a response of any total duration survives as
    /// long as the upstream keeps producing.
    ///
    /// A breach means the upstream stalled after it had already answered, which says nothing
    /// conclusive about backend health, so it abandons the circuit-breaker probe rather than
    /// recording a failure. Applies to non-streaming responses too: transferring a large body is not
    /// evidence of ill health either.
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
