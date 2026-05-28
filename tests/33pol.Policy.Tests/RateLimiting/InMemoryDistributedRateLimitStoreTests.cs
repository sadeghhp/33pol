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
    public void TryAcquireRequest_WhenPartitionBecomesStale_RemovesOldPartitionState()
    {
        var store = CreateStore(retentionSeconds: 60, compactEveryOperations: 1);
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("stale-partition", TightPolicy, now).IsAcquired.Should().BeTrue();
        GetRequestWindowCount(store).Should().Be(1);

        var later = now.AddMinutes(2);
        store.TryAcquireRequest("active-partition", TightPolicy, later).IsAcquired.Should().BeTrue();

        GetRequestWindowCount(store).Should().Be(1);
    }

    [Fact]
    public void TryAcquireStreamSlot_WhenPartitionBecomesStale_RemovesOldStreamPartition()
    {
        var store = CreateStore(retentionSeconds: 1, compactEveryOperations: 1);
        var policy = new RateLimitPolicy(100, 10, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("stale-stream", policy).IsAcquired.Should().BeTrue();
        store.ReleaseStreamSlot("stale-stream");
        GetStreamSlotCount(store).Should().Be(1);

        Thread.Sleep(TimeSpan.FromMilliseconds(1100));
        store.TryAcquireRequest(
            "compaction-trigger",
            TightPolicy,
            DateTimeOffset.UtcNow.AddMinutes(2)).IsAcquired.Should().BeTrue();

        GetStreamSlotCount(store).Should().Be(0);
    }

    private static InMemoryDistributedRateLimitStore CreateStore(
        int retentionSeconds = 3600,
        int compactEveryOperations = 256)
    {
        var options = Options.Create(new RateLimitingOptions
        {
            InMemoryPartitionRetentionSeconds = retentionSeconds,
            InMemoryCompactionEveryOperations = compactEveryOperations,
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
