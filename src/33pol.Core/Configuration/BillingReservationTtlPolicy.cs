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
            Math.Max(0, resilience.ForwardTimeoutSeconds) +
            Math.Max(0, resilience.StreamIdleTimeoutSeconds);

        var flushDelaySeconds = (int)Math.Ceiling(Math.Max(0, billing.UsageWriterFlushIntervalMs) / 1000d);

        return Math.Max(
            MinimumSeconds,
            maxInFlightSeconds + flushDelaySeconds + SafetyMarginSeconds);
    }

    public static bool IsSufficient(GatewayResilienceOptions resilience, BillingOptions billing) =>
        billing.BudgetReservationTtlSeconds >= MinimumTtlSeconds(resilience, billing);

    public static string DescribeInsufficient(GatewayResilienceOptions resilience, BillingOptions billing)
    {
        var minimum = MinimumTtlSeconds(resilience, billing);
        return
            $"{BillingOptions.SectionName}.{nameof(BillingOptions.BudgetReservationTtlSeconds)} " +
            $"({billing.BudgetReservationTtlSeconds}s) must be at least {minimum}s: the longest possible " +
            $"in-flight request is {resilience.ForwardTimeoutSeconds}s waiting for headers plus " +
            $"{resilience.StreamIdleTimeoutSeconds}s of streaming, and usage settles up to " +
            $"{billing.UsageWriterFlushIntervalMs}ms later. A shorter TTL reclaims reservations for " +
            "requests that are still running, which lets concurrent requests overshoot a hard-stop budget.";
    }
}
