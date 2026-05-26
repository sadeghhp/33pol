namespace Pol33.Core.RateLimiting;

public sealed record RateLimitAcquireResult(
    bool IsAcquired,
    GatewayRateLimitReason? RejectionReason = null,
    int? RetryAfterSeconds = null);

public enum GatewayRateLimitReason
{
    RateLimitExceeded,
    ConcurrencyLimitExceeded,
}
