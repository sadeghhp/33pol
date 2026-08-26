using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// The peek/debit pair, used where the cost of a request is only known after it has been answered —
/// the auth-failure budget charges the outcome, not the attempt.
/// </summary>
public sealed class InMemoryDistributedRateLimitStorePeekTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PeekRequest_DoesNotConsumeATokenOrCreateAPartition()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 2, Burst: 0, MaxConcurrentStreams: 0);

        for (var i = 0; i < 50; i++)
        {
            store.PeekRequest("t", policy, Now).IsAcquired.Should().BeTrue();
        }

        // The full budget is still there to spend.
        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void DebitRequest_ChargesTheBucketAndPeekThenReportsItEmpty()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 2, Burst: 0, MaxConcurrentStreams: 0);

        store.DebitRequest("t", policy, Now);
        store.PeekRequest("t", policy, Now).IsAcquired.Should().BeTrue();

        store.DebitRequest("t", policy, Now);
        var exhausted = store.PeekRequest("t", policy, Now);

        exhausted.IsAcquired.Should().BeFalse();
        exhausted.RejectionReason.Should().Be(GatewayRateLimitReason.RateLimitExceeded);
        exhausted.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// A debit past empty floors at zero. Letting the bucket go negative would make the partition
    /// serve out several windows of penance for one burst it was never admitted for.
    /// </summary>
    [Fact]
    public void DebitRequest_PastEmpty_FloorsAtZeroRatherThanGoingNegative()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);

        for (var i = 0; i < 500; i++)
        {
            store.DebitRequest("t", policy, Now);
        }

        // 60 rpm is a token a second: one second after the flood the partition is usable again.
        store.PeekRequest("t", policy, Now.AddSeconds(1)).IsAcquired.Should().BeTrue();
    }

    /// <summary>Budget reporting is what the response headers are built from.</summary>
    [Fact]
    public void TryAcquireRequest_ReportsLimitRemainingAndReset()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 40, MaxConcurrentStreams: 0);

        var first = store.TryAcquireRequest("t", policy, Now);

        first.Limit.Should().Be(100);
        first.Remaining.Should().Be(99);
        first.ResetAfterSeconds.Should().Be(1, "60 rpm is one token a second, and one token is missing");

        for (var i = 0; i < 99; i++)
        {
            store.TryAcquireRequest("t", policy, Now);
        }

        var refused = store.TryAcquireRequest("t", policy, Now);
        refused.IsAcquired.Should().BeFalse();
        refused.Limit.Should().Be(100);
        refused.Remaining.Should().Be(0);
        refused.ResetAfterSeconds.Should().Be(100);
    }

    [Fact]
    public void TryAcquireStreamSlot_ReportsRemainingSlots()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 2);

        store.TryAcquireStreamSlot("t", policy).Remaining.Should().Be(1);
        store.TryAcquireStreamSlot("t", policy).Remaining.Should().Be(0);

        var refused = store.TryAcquireStreamSlot("t", policy);
        refused.IsAcquired.Should().BeFalse();
        refused.Limit.Should().Be(2);
        refused.Remaining.Should().Be(0);
    }

    /// <summary>
    /// A tier with no stream cap is unlimited, not denied — and reports no budget, so nothing
    /// publishes a limit that is not being enforced.
    /// </summary>
    [Fact]
    public void TryAcquireStreamSlot_WithZeroCap_IsUnlimited()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);

        for (var i = 0; i < 100; i++)
        {
            store.TryAcquireStreamSlot("t", policy).IsAcquired.Should().BeTrue();
        }

        store.TryAcquireStreamSlot("t", policy).Limit.Should().BeNull();
    }

    /// <summary>
    /// A clock that steps backwards — an NTP correction, or a caller handing over timestamps out of
    /// order — must not leave a stale "last refill" behind, or the partition collects a windfall of
    /// tokens for the same seconds twice once time catches up.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WhenTheClockStepsBackwards_GrantsNoWindfall()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);

        for (var i = 0; i < 60; i++)
        {
            store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeTrue();
        }

        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeFalse();

        // Clock jumps a minute back, then returns to where it was.
        store.TryAcquireRequest("t", policy, Now.AddMinutes(-1)).IsAcquired.Should().BeFalse();
        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should()
            .BeFalse("the minute that was replayed must not refill the bucket a second time");

        // Genuine forward progress still refills at the configured rate.
        store.TryAcquireRequest("t", policy, Now.AddSeconds(1)).IsAcquired.Should().BeTrue();
    }

    /// <summary>
    /// A partition being rejected is touched on every rejection, so the sweep cannot decide it is
    /// idle and evict it from under itself — which would hand it a full bucket.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WhenTheClockStepsBackwards_DoesNotAgeThePartitionForwards()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var policy = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);

        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, Now.AddHours(-2)).IsAcquired.Should().BeFalse();
        store.TryAcquireRequest("t", policy, Now).IsAcquired.Should().BeFalse();
    }
}
