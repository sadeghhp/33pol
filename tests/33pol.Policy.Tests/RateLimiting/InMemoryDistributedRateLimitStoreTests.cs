using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

public sealed class InMemoryDistributedRateLimitStoreTests
{
    private static readonly RateLimitPolicy TightPolicy = new(Rpm: 2, Burst: 0, MaxConcurrentStreams: 1);

    [Fact]
    public void TryAcquireRequest_UnderLimit_Passes()
    {
        var store = CreateStore();
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t1", TightPolicy, now).IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void TryAcquireRequest_OverRpm_ReturnsRateLimitExceededWithRetryAfter()
    {
        var store = CreateStore();
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
        var store = CreateStore();
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
        var second = store.TryAcquireStreamSlot("t1", policy);

        second.IsAcquired.Should().BeFalse();
        second.RejectionReason.Should().Be(GatewayRateLimitReason.ConcurrencyLimitExceeded);
    }

    [Fact]
    public void ReleaseStreamSlot_AllowsAnotherAcquire()
    {
        var store = CreateStore();
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeFalse();

        store.ReleaseStreamSlot("t1");

        store.TryAcquireStreamSlot("t1", policy).IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void Compact_WhenPartitionBecomesStale_RemovesOldPartitionState()
    {
        var store = CreateStore(retentionSeconds: 60);
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("stale-partition", TightPolicy, now).IsAcquired.Should().BeTrue();
        GetRequestWindowCount(store).Should().Be(1);

        var later = now.AddMinutes(2);
        store.TryAcquireRequest("active-partition", TightPolicy, later).IsAcquired.Should().BeTrue();
        store.Compact(later);

        GetRequestWindowCount(store).Should().Be(1);
    }

    [Fact]
    public void Compact_WhenStreamPartitionBecomesStale_RemovesIt()
    {
        var store = CreateStore(retentionSeconds: 1);
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("stale-stream", policy).IsAcquired.Should().BeTrue();
        store.ReleaseStreamSlot("stale-stream");
        GetStreamSlotCount(store).Should().Be(1);

        store.Compact(DateTimeOffset.UtcNow.AddMinutes(2));

        GetStreamSlotCount(store).Should().Be(0);
    }

    private static InMemoryDistributedRateLimitStore CreateStore(int retentionSeconds = 3600)
    {
        var options = Options.Create(new RateLimitingOptions
        {
            InMemoryPartitionRetentionSeconds = retentionSeconds,
        });

        return new InMemoryDistributedRateLimitStore(options);
    }

    private static int GetRequestWindowCount(InMemoryDistributedRateLimitStore store)
    {
        var field = typeof(InMemoryDistributedRateLimitStore)
            .GetField("_requestWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dictionary = field.GetValue(store)!;
        return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
    }

    private static int GetStreamSlotCount(InMemoryDistributedRateLimitStore store)
    {
        var field = typeof(InMemoryDistributedRateLimitStore)
            .GetField("_streamSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dictionary = field.GetValue(store)!;
        return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
    }
}

public sealed class InMemoryDistributedRateLimitStoreTokenBucketTests
{
    private static InMemoryDistributedRateLimitStore CreateStore() => new();

    /// <summary>
    /// The bucket refills continuously, so a rejected caller is told to retry after roughly one
    /// token's worth of time — not until the next minute boundary. This is what stops SDKs (which
    /// honour Retry-After) from sleeping for most of a minute and reading it as the gateway
    /// queueing them.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WhenExhausted_RetryAfterIsOneTokenNotOneMinute()
    {
        var store = CreateStore();
        var policy = new RateLimitPolicy(Rpm: 600, Burst: 100, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 1, TimeSpan.Zero);

        for (var i = 0; i < 700; i++)
        {
            store.TryAcquireRequest("t", policy, now).IsAcquired.Should().BeTrue();
        }

        var rejected = store.TryAcquireRequest("t", policy, now);
        rejected.IsAcquired.Should().BeFalse();
        rejected.RetryAfterSeconds.Should().Be(1, "600 rpm refills a token every 100 ms, so the wait rounds up to 1 s");
    }

    /// <summary>A partition that has drained its bucket earns tokens back at Rpm/60 per second.</summary>
    [Fact]
    public void TryAcquireRequest_AfterExhaustion_RefillsAtTheConfiguredRate()
    {
        var store = CreateStore();
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 1, TimeSpan.Zero);

        for (var i = 0; i < 60; i++)
        {
            store.TryAcquireRequest("t", policy, now).IsAcquired.Should().BeTrue();
        }

        store.TryAcquireRequest("t", policy, now).IsAcquired.Should().BeFalse();
        store.TryAcquireRequest("t", policy, now.AddMilliseconds(500)).IsAcquired.Should().BeFalse();

        // One second later exactly one token has accrued: one request passes, the next does not.
        store.TryAcquireRequest("t", policy, now.AddSeconds(1)).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, now.AddSeconds(1)).IsAcquired.Should().BeFalse();

        // Ten more seconds: ten tokens.
        var admitted = 0;
        for (var i = 0; i < 20; i++)
        {
            if (store.TryAcquireRequest("t", policy, now.AddSeconds(11)).IsAcquired)
            {
                admitted++;
            }
        }

        admitted.Should().Be(10);
    }

    /// <summary>The bucket never overfills: a long idle period grants at most Rpm + Burst.</summary>
    [Fact]
    public void TryAcquireRequest_AfterLongIdle_CapsAtCapacity()
    {
        var store = CreateStore();
        var policy = new RateLimitPolicy(Rpm: 10, Burst: 5, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 1, TimeSpan.Zero);

        store.TryAcquireRequest("t", policy, now).IsAcquired.Should().BeTrue();

        var later = now.AddHours(3);
        var admitted = 0;
        for (var i = 0; i < 40; i++)
        {
            if (store.TryAcquireRequest("t", policy, later).IsAcquired)
            {
                admitted++;
            }
        }

        admitted.Should().Be(15);
    }
}
