using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IDistributedRateLimitStore
{
    RateLimitAcquireResult TryAcquireRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now);

    /// <summary>
    /// Reports what <see cref="TryAcquireRequest"/> would answer right now <em>without</em> taking a
    /// token, for callers that only debit the partition once the request's outcome is known.
    /// </summary>
    /// <remarks>
    /// Refilling is still applied, so a peek never reports a stale fill. Because the check and the
    /// later debit are two separate operations, concurrent callers can each be told there is a token
    /// and then debit past the limit — acceptable where the peek guards a rejection path rather than
    /// the metered one.
    /// </remarks>
    RateLimitAcquireResult PeekRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now);

    /// <summary>Takes one token without a limit check, creating the partition if it does not exist.</summary>
    /// <remarks>
    /// The counterpart to <see cref="PeekRequest"/>: the decision was already made, this only records
    /// the cost. The bucket floors at zero rather than going negative.
    /// </remarks>
    void DebitRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now);

    RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy);

    void ReleaseStreamSlot(string partitionKey);
}
