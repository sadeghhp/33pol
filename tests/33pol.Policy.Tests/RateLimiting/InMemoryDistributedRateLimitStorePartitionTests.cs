using System.Reflection;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Housekeeping: the partition ceiling, what eviction is allowed to touch, and the guards that keep
/// a sweep — which walks every live partition on a request thread — from running on every request.
/// </summary>
public sealed class InMemoryDistributedRateLimitStorePartitionTests
{
    private static readonly RateLimitPolicy Policy = new(Rpm: 60, Burst: 0, MaxConcurrentStreams: 2);
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Anonymous traffic partitions by client address, so without a ceiling a caller spread across
    /// an address range holds one entry per address for the whole retention window.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_PastThePartitionCeiling_EvictsDownToIt()
    {
        var store = CreateStore(maxPartitions: 10, compactEveryOperations: 1, minCompactionIntervalSeconds: 0);

        for (var i = 0; i < 200; i++)
        {
            store.TryAcquireRequest($"anon:10.0.0.{i}", Policy, Start.AddMilliseconds(i));
        }

        RequestWindowCount(store).Should().BeLessThanOrEqualTo(10);
    }

    /// <summary>
    /// Eviction takes the least-recently-seen partitions. A partition being actively rejected is
    /// touched on every rejection, so it must never be the one dropped — dropping it would hand it
    /// a fresh, full bucket, which is exactly the limit bypass a flood of new partitions would buy.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WhenCeilingForcesEviction_KeepsTheActivelyRejectedPartition()
    {
        var store = CreateStore(maxPartitions: 5, compactEveryOperations: 1, minCompactionIntervalSeconds: 0);
        var tight = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var now = Start;

        // Drain the victim's bucket so every later attempt is a rejection.
        store.TryAcquireRequest("victim", tight, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("victim", tight, now).IsAcquired.Should().BeFalse();

        // Flood the table well past the ceiling, keeping the victim being refused throughout.
        for (var i = 0; i < 100; i++)
        {
            now = now.AddMilliseconds(10);
            store.TryAcquireRequest($"anon:10.0.0.{i}", tight, now);
            store.TryAcquireRequest("victim", tight, now).IsAcquired.Should()
                .BeFalse("an evicted partition would come back with a full bucket");
        }
    }

    /// <summary>A partition holding stream slots is not evicted, at any threshold.</summary>
    [Fact]
    public void TryAcquireStreamSlot_PastThePartitionCeiling_NeverEvictsAPartitionHoldingSlots()
    {
        var time = new FakeTimeProvider(Start);
        var store = CreateStore(
            maxPartitions: 5,
            compactEveryOperations: 1,
            minCompactionIntervalSeconds: 0,
            timeProvider: time);

        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeTrue();

        for (var i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            store.TryAcquireStreamSlot($"anon:10.0.0.{i}", Policy);
        }

        // Still counted: the slot survived the churn, so the second slot is the last one free.
        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("holder", Policy).IsAcquired.Should().BeFalse();
    }

    /// <summary>
    /// A partition parked at its stream cap must still drive sweeps; otherwise a gateway whose
    /// traffic is all rejected streams never compacts at all.
    /// </summary>
    [Fact]
    public void TryAcquireStreamSlot_WhenRejected_StillDrivesCompaction()
    {
        var time = new FakeTimeProvider(Start);
        var store = CreateStore(
            retentionSeconds: 60,
            compactEveryOperations: 1,
            minCompactionIntervalSeconds: 0,
            timeProvider: time);
        var single = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 1);

        store.TryAcquireRequest("goes-stale", Policy, Start);
        RequestWindowCount(store).Should().Be(1);

        store.TryAcquireStreamSlot("busy", single).IsAcquired.Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(5));

        // Only rejections from here on: the slot is held and never released.
        store.TryAcquireStreamSlot("busy", single).IsAcquired.Should().BeFalse();

        RequestWindowCount(store).Should().Be(0, "the rejected stream acquire swept the stale request window");
    }

    /// <summary>
    /// The operation counter alone is not a bound on sweep cost — under load it fires several times
    /// a second, and each sweep walks every live partition. The interval floor is what keeps that
    /// proportional to wall-clock instead of to traffic.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WithinTheCompactionInterval_DoesNotSweep()
    {
        var store = CreateStore(
            retentionSeconds: 1,
            compactEveryOperations: 1,
            minCompactionIntervalSeconds: 30);

        store.TryAcquireRequest("first", Policy, Start);
        RequestWindowCount(store).Should().Be(1);

        // Long past the retention window, but only seconds past the last sweep.
        store.TryAcquireRequest("second", Policy, Start.AddSeconds(10));
        RequestWindowCount(store).Should().Be(2, "the interval floor deferred the sweep");

        store.TryAcquireRequest("third", Policy, Start.AddSeconds(45));
        RequestWindowCount(store).Should().Be(1, "past the floor, the sweep ran and both idle partitions went");
    }

    /// <summary>Passing the ceiling overrides the interval floor: deferring is what let it grow.</summary>
    [Fact]
    public void TryAcquireRequest_PastTheCeiling_SweepsEvenInsideTheInterval()
    {
        var store = CreateStore(
            maxPartitions: 4,
            compactEveryOperations: 1,
            minCompactionIntervalSeconds: 3600);

        for (var i = 0; i < 50; i++)
        {
            store.TryAcquireRequest($"anon:10.0.0.{i}", Policy, Start.AddMilliseconds(i));
        }

        // Every sweep after the first is inside the interval floor, so only the ceiling can have
        // forced them.
        RequestWindowCount(store).Should().BeLessThanOrEqualTo(5, "at most one partition added since the last sweep");
    }

    private static InMemoryDistributedRateLimitStore CreateStore(
        int retentionSeconds = 3600,
        int compactEveryOperations = 256,
        int minCompactionIntervalSeconds = 5,
        int maxPartitions = 50_000,
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(new RateLimitingOptions
            {
                InMemoryPartitionRetentionSeconds = retentionSeconds,
                InMemoryCompactionEveryOperations = compactEveryOperations,
                InMemoryCompactionMinIntervalSeconds = minCompactionIntervalSeconds,
                InMemoryMaxPartitions = maxPartitions,
            }),
            timeProvider);

    private static int RequestWindowCount(InMemoryDistributedRateLimitStore store) =>
        DictionaryCount(store, "_requestWindows");

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
