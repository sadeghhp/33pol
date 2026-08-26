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
public sealed record RateLimitAcquireResult(
    bool IsAcquired,
    GatewayRateLimitReason? RejectionReason = null,
    int? RetryAfterSeconds = null,
    int? Limit = null,
    int? Remaining = null,
    int? ResetAfterSeconds = null);

public enum GatewayRateLimitReason
{
    RateLimitExceeded,
    ConcurrencyLimitExceeded,
}
