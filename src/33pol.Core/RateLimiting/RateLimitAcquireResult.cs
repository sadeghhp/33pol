namespace Pol33.Core.RateLimiting;

/// <summary>
/// The outcome of an admission decision, plus what the caller needs to tell the client about its
/// budget.
/// </summary>
/// <param name="IsAcquired">Whether the request was admitted.</param>
/// <param name="RejectionReason">Which control refused it; null when admitted.</param>
/// <param name="RetryAfterSeconds">Seconds to wait before retrying; null when admitted.</param>
/// <param name="Limit">
/// The partition's total budget — bucket capacity for request rate, the slot count for stream
/// concurrency. Null when the control is not enforced for this tier.
/// </param>
/// <param name="Remaining">Whole units left after this decision, never negative.</param>
/// <param name="ResetAfterSeconds">
/// Seconds until the partition is back at its full budget at the current refill rate. Zero when it
/// already is.
/// </param>
/// <param name="Scope">
/// Which scope this reading describes. On a rejection it is the scope that refused; on an admission
/// it is the <em>tightest</em> scope — the one closest to refusing — because that is the number a
/// client should pace itself against. Null for the degenerate "nothing is enforced" result.
/// </param>
/// <param name="PartitionKey">The bucket the reading came from, for logs and reports; never sent to the client.</param>
/// <param name="AdaptiveFactor">
/// What the adaptive governor multiplied the configured rate by to reach <paramref name="Limit"/>.
/// <c>1.0</c> means the configured limit is being enforced unchanged.
/// </param>
public sealed record RateLimitAcquireResult(
    bool IsAcquired,
    GatewayRateLimitReason? RejectionReason = null,
    int? RetryAfterSeconds = null,
    int? Limit = null,
    int? Remaining = null,
    int? ResetAfterSeconds = null,
    RateLimitScope? Scope = null,
    string? PartitionKey = null,
    double AdaptiveFactor = 1.0)
{
    /// <summary>Admitted, with nothing to report — no scope in the rule set enforces this control.</summary>
    public static RateLimitAcquireResult Unlimited { get; } = new(true);

    /// <summary>Which of the two controls produced this reading.</summary>
    public RateLimitControl Control =>
        RejectionReason == GatewayRateLimitReason.ConcurrencyLimitExceeded
            ? RateLimitControl.Concurrency
            : RateLimitControl.Rate;
}

public enum GatewayRateLimitReason
{
    RateLimitExceeded,
    ConcurrencyLimitExceeded,
}
