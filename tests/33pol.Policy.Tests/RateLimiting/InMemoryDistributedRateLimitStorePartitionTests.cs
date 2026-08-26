using System.Reflection;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Housekeeping: the partition ceiling, what eviction is allowed to touch, and the fact that none of
/// it happens on a request thread.
/// </summary>
public sealed class InMemoryDistributedRateLimitStorePartitionTests
{
    private static readonly RateLimitPolicy Policy = new(Rpm: 60, Burst: 0, MaxConcurrentStreams: 2);
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A sweep walks every live partition and, past the ceiling, copies and sorts the whole table.
    /// None of that may land on a request: admitting a request must leave the table exactly as it
    /// found it, growing it if anything, and only the maintenance service may shrink it.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_NeverCompacts_OnlyTheMaintenanceSweepDoes()
    {
        var store = CreateStore(retentionSeconds: 1);

        store.TryAcquireRequest("goes-stale", Policy, Start);
        RequestWindowCount(store).Should().Be(1);

        // Minutes past the retention window, and hundreds of operations later: still there, because
        // nothing on the request path sweeps.
        for (var i = 0; i < 500; i++)
        {
            store.TryAcquireRequest("active", Policy, Start.AddMinutes(5));
        }

        RequestWindowCount(store).Should().Be(2, "requests do not sweep");

        store.Compact(Start.AddMinutes(5));

        RequestWindowCount(store).Should().Be(1, "the sweep removed the idle partition and kept the active one");
    }

    /// <summary>
    /// Anonymous traffic partitions by client address block, so without a ceiling a caller spread
    /// across an address range holds one entry per block for the whole retention window.
    /// </summary>
    [Fact]
    public void Compact_PastThePartitionCeiling_EvictsDownToIt()
    {
        var store = CreateStore(maxPartitions: 10);

        for (var i = 0; i < 200; i++)
        {
            store.TryAcquireRequest($"anon:10.0.0.{i}", Policy, Start.AddMilliseconds(i));
        }

        store.Compact(Start.AddSeconds(1));

        RequestWindowCount(store).Should().BeLessThanOrEqualTo(10);
        store.GetStats().RequestPartitions.Should().Be(
            RequestWindowCount(store),
            "the tracked count must not drift from the table it counts");
    }

    /// <summary>
    /// Eviction takes the least-recently-seen partitions. A partition being actively rejected is
    /// touched on every rejection, so it must never be the one dropped — dropping it would hand it
    /// a fresh, full bucket, which is exactly the limit bypass a flood of new partitions would buy.
    /// </summary>
    [Fact]
    public void Compact_WhenCeilingForcesEviction_KeepsTheActivelyRejectedPartition()
    {
        var store = CreateStore(maxPartitions: 5);
        var tight = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var now = Start;

        // Drain the victim's bucket so every later attempt is a rejection.
        store.TryAcquireRequest("victim", tight, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("victim", tight, now).IsAcquired.Should().BeFalse();

        // Flood the table well past the ceiling, sweeping throughout, keeping the victim refused.
        for (var i = 0; i < 100; i++)
        {
            now = now.AddMilliseconds(10);
            store.TryAcquireRequest($"anon:10.0.0.{i}", tight, now);
            store.Compact(now);
            store.TryAcquireRequest("victim", tight, now).IsAcquired.Should()
                .BeFalse("an evicted partition would come back with a full bucket");
        }
    }

    /// <summary>A partition holding stream slots is not evicted, at any threshold.</summary>
    [Fact]
    public void Compact_PastThePartitionCeiling_NeverEvictsAPartitionHoldingSlots()
    {
        var time = new FakeTimeProvider(Start);
        var store = CreateStore(maxPartitions: 5, timeProvider: time);

        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeTrue();

        for (var i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            store.TryAcquireStreamSlot($"anon:10.0.0.{i}", Policy);
            store.Compact(time.GetUtcNow());
        }

        // Still counted: the slot survived the churn, so the second slot is the last one free.
        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeFalse();
    }

    /// <summary>An idle stream partition is swept once it is past the retention window.</summary>
    [Fact]
    public void Compact_IdleStreamPartition_IsRemoved()
    {
        var time = new FakeTimeProvider(Start);
        var store = CreateStore(retentionSeconds: 60, timeProvider: time);
        var single = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 1);

        store.TryAcquireStreamSlot("idle", single).IsAcquired.Should().BeTrue();
        store.ReleaseStreamSlot("idle");
        StreamSlotCount(store).Should().Be(1);

        time.Advance(TimeSpan.FromMinutes(5));
        store.Compact(time.GetUtcNow());

        StreamSlotCount(store).Should().Be(0);
        store.GetStats().StreamPartitions.Should().Be(0);
    }

    private static InMemoryDistributedRateLimitStore CreateStore(
        int retentionSeconds = 3600,
        int maxPartitions = 50_000,
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(new RateLimitingOptions
            {
                InMemoryPartitionRetentionSeconds = retentionSeconds,
                InMemoryMaxPartitions = maxPartitions,
            }),
            timeProvider);

    private static int RequestWindowCount(InMemoryDistributedRateLimitStore store) =>
        DictionaryCount(store, "_requestWindows");

    private static int StreamSlotCount(InMemoryDistributedRateLimitStore store) =>
        DictionaryCount(store, "_streamSlots");

    private static int DictionaryCount(InMemoryDistributedRateLimitStore store, string fieldName)
    {
        var field = typeof(InMemoryDistributedRateLimitStore)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dictionary = field.GetValue(store)!;
        return (int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)!;
    }

    /// <summary>
    /// The stream-slot path reads the clock for itself rather than taking a timestamp, so eviction
    /// there is only testable once that clock is injectable.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
