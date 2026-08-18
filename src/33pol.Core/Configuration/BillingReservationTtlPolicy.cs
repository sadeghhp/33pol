namespace Pol33.Core.Configuration;

/// <summary>
/// Derives the minimum safe <see cref="BillingOptions.BudgetReservationTtlSeconds"/> from the
/// timings that actually bound a request's life.
/// </summary>
/// <remarks>
/// A reservation must outlive the request it belongs to. If it expires first, the ledger reports
/// headroom that is already spoken for, and concurrent long requests can each be admitted against
/// the same remaining allowance — overshooting a hard-stop budget by a multiple of its limit.
///
/// The bound is the worst-case in-flight time (waiting for headers, then a stream that keeps
/// producing right up to the idle limit) plus the time usage sits in the batch writer before it is
/// persisted and the reservation is settled, plus a margin for scheduling and database latency.
///
/// The header wait is not just <see cref="GatewayResilienceOptions.ForwardTimeoutSeconds"/>: the
/// forwarder widens it per megabyte of request body
/// (<see cref="GatewayResilienceOptions.ForwardTimeoutSecondsPerRequestMegabyte"/>) up to
/// <see cref="GatewayResilienceOptions.MaxForwardTimeoutSeconds"/>, so a request at the body cap
/// can legitimately wait far longer than the base allowance. The policy therefore uses the
/// allowance a maximum-size body would receive — precisely the long-context traffic whose
/// reservations are largest and whose expiry mid-flight would matter most.
/// </remarks>
public static class BillingReservationTtlPolicy
{
    /// <summary>Absolute floor, so a deployment with tiny timeouts still gets a usable backstop.</summary>
    public const int MinimumSeconds = 60;

    /// <summary>Slack for flush scheduling, database round-trips and clock granularity.</summary>
    public const int SafetyMarginSeconds = 60;

    public static int MinimumTtlSeconds(GatewayResilienceOptions resilience, BillingOptions billing)
    {
        ArgumentNullException.ThrowIfNull(resilience);
        ArgumentNullException.ThrowIfNull(billing);

        var maxInFlightSeconds =
            MaxHeaderWaitSeconds(resilience) +
            Math.Max(0, resilience.StreamIdleTimeoutSeconds);

        var flushDelaySeconds = (int)Math.Ceiling(Math.Max(0, billing.UsageWriterFlushIntervalMs) / 1000d);

        return Math.Max(
            MinimumSeconds,
            maxInFlightSeconds + flushDelaySeconds + SafetyMarginSeconds);
    }

    /// <summary>
    /// The longest header allowance any request can receive: the base timeout scaled for a body at
    /// <see cref="GatewayResilienceOptions.MaxRequestBodyBytes"/>, capped by
    /// <see cref="GatewayResilienceOptions.MaxForwardTimeoutSeconds"/>, and never below the base.
    /// Mirrors the forwarder's per-request computation at its maximum.
    /// </summary>
    public static int MaxHeaderWaitSeconds(GatewayResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(resilience);

        var baseSeconds = Math.Max(0, resilience.ForwardTimeoutSeconds);
        var perMb = Math.Max(0, resilience.ForwardTimeoutSecondsPerRequestMegabyte);
        var megabytes = (long)Math.Ceiling(Math.Max(0L, resilience.MaxRequestBodyBytes) / (1024d * 1024d));
        var scaled = baseSeconds + Math.Min(int.MaxValue - baseSeconds, perMb * megabytes);
        var capped = Math.Min(scaled, Math.Max(0, resilience.MaxForwardTimeoutSeconds));

        return (int)Math.Max(baseSeconds, capped);
    }

    public static bool IsSufficient(GatewayResilienceOptions resilience, BillingOptions billing) =>
        billing.BudgetReservationTtlSeconds >= MinimumTtlSeconds(resilience, billing);

    public static string DescribeInsufficient(GatewayResilienceOptions resilience, BillingOptions billing)
    {
        var minimum = MinimumTtlSeconds(resilience, billing);
        var headerWait = MaxHeaderWaitSeconds(resilience);
        return
            $"{BillingOptions.SectionName}.{nameof(BillingOptions.BudgetReservationTtlSeconds)} " +
            $"({billing.BudgetReservationTtlSeconds}s) must be at least {minimum}s: the longest possible " +
            $"in-flight request is {headerWait}s waiting for headers " +
            $"({nameof(GatewayResilienceOptions.ForwardTimeoutSeconds)} {resilience.ForwardTimeoutSeconds}s scaled by " +
            $"{nameof(GatewayResilienceOptions.ForwardTimeoutSecondsPerRequestMegabyte)} {resilience.ForwardTimeoutSecondsPerRequestMegabyte}s/MB " +
            $"for a body at {nameof(GatewayResilienceOptions.MaxRequestBodyBytes)}, capped at " +
            $"{nameof(GatewayResilienceOptions.MaxForwardTimeoutSeconds)} {resilience.MaxForwardTimeoutSeconds}s) plus " +
            $"{resilience.StreamIdleTimeoutSeconds}s of streaming, and usage settles up to " +
            $"{billing.UsageWriterFlushIntervalMs}ms later. A shorter TTL reclaims reservations for " +
            "requests that are still running, which lets concurrent requests overshoot a hard-stop budget.";
    }
}
