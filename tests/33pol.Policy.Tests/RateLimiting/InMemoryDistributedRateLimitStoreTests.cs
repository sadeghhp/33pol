using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

public sealed class InMemoryDistributedRateLimitStoreTests
{
    private static readonly RateLimitPolicy TightPolicy = new(Rpm: 2, Burst: 0, MaxConcurrentStreams: 1);

    [Fact]
    public void TryAcquireRequest_UnderLimit_Passes()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void TryAcquireRequest_OverRpm_ReturnsRateLimitExceededWithRetryAfter()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
        var third = store.TryAcquireRequest("t1", TightPolicy, now);

        third.IsAcquired.Should().BeFalse();
        third.RejectionReason.Should().Be(GatewayRateLimitReason.RateLimitExceeded);
        third.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TryAcquireStreamSlot_SecondConcurrentRequest_Rejected()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
        var second = store.TryAcquireStreamSlot("t1", policy);

        second.IsAcquired.Should().BeFalse();
        second.RejectionReason.Should().Be(GatewayRateLimitReason.ConcurrencyLimitExceeded);
    }

    [Fact]
    public void ReleaseStreamSlot_AllowsAnotherAcquire()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeFalse();

        store.ReleaseStreamSlot("t1");

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
    }
}
