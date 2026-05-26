using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IDistributedRateLimitStore
{
    RateLimitAcquireResult TryAcquireRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now);

    RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy);

    void ReleaseStreamSlot(string partitionKey);
}
