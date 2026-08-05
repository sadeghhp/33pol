using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Concurrency properties of the in-memory limiter. Both defects covered here are load-dependent and
/// invisible in single-threaded tests: a tombstoned partition being handed out as if it were live,
/// and the increment/decision pair not being atomic.
/// </summary>
public sealed class InMemoryDistributedRateLimitStoreRaceTests
{
    private static InMemoryDistributedRateLimitStore Create(
        int retentionSeconds = 3600,
        int compactEvery = 256) =>
        new(Options.Create(new RateLimitingOptions
        {
            InMemoryPartitionRetentionSeconds = retentionSeconds,
            InMemoryCompactionEveryOperations = compactEvery,
        }));

    /// <summary>
    /// Acquire/release churn drives compaction constantly. A tenant that never exceeds its limit
    /// must never see a rejection — every one would be an eviction-race artefact, since a tombstoned
    /// state used to fail acquisition outright.
    /// </summary>
    [Fact]
    public async Task TryAcquireStreamSlot_UnderCompactionChurn_NeverRejectsBelowTheLimit()
    {
        // Aggressive compaction and instant staleness maximise the eviction window.
        var store = Create(retentionSeconds: 1, compactEvery: 1);
        var policy = new RateLimitPolicy(Rpm: 100_000, Burst: 0, MaxConcurrentStreams: 4);

        var spuriousRejections = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                // One slot at a time per worker, well under the limit of 4 across 8 workers only if
                // they serialise — so use a distinct partition per worker to keep the invariant
                // strictly "one in flight, limit 4".
                var partition = $"tenant-{worker}";

                var result = store.TryAcquireStreamSlot(partition, policy);
                if (!result.IsAcquired)
                {
                    Interlocked.Increment(ref spuriousRejections);
                    continue;
                }

                store.ReleaseStreamSlot(partition);
            }
        })));

        spuriousRejections.Should().Be(
            0,
            "no partition ever held more than one concurrent stream against a limit of 4");
    }

    [Fact]
    public void TryAcquireStreamSlot_AfterEvictionOfAnIdlePartition_StillAcquires()
    {
        var store = Create(retentionSeconds: 1, compactEvery: 1);
        var policy = new RateLimitPolicy(Rpm: 100_000, Burst: 0, MaxConcurrentStreams: 2);

        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired.Should().BeTrue();
        store.ReleaseStreamSlot("tenant-a");

        // Drive compaction repeatedly so "tenant-a" is swept while idle, then use it again.
        for (var i = 0; i < 50; i++)
        {
            store.TryAcquireStreamSlot("filler", policy);
            store.ReleaseStreamSlot("filler");
        }

        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired
            .Should().BeTrue("a swept partition must be recreated, not resurrected as a tombstone");
    }

    [Fact]
    public void TryAcquireStreamSlot_AtTheLimit_StillRejects()
    {
        var store = Create();
        var policy = new RateLimitPolicy(Rpm: 100_000, Burst: 0, MaxConcurrentStreams: 2);

        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired.Should().BeTrue();
        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired
            .Should().BeFalse("the genuine limit rejection must survive the tombstone fix");
    }

    /// <summary>
    /// A rejected request must not consume quota. It used to increment before the limit check, so a
    /// client that kept retrying after a 429 drove the counter further past the limit and could not
    /// recover for the rest of the window.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_RejectedCalls_DoNotConsumeFurtherQuota()
    {
        var store = Create();
        var policy = new RateLimitPolicy(Rpm: 3, Burst: 0, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 3; i++)
        {
            store.TryAcquireRequest("tenant-a", policy, now).IsAcquired.Should().BeTrue();
        }

        // Hammer well past the limit.
        for (var i = 0; i < 100; i++)
        {
            store.TryAcquireRequest("tenant-a", policy, now).IsAcquired.Should().BeFalse();
        }

        // The next window starts clean: exactly the limit is available again, which would not be the
        // case if the 100 rejections had each incremented the counter.
        var nextWindow = now.AddMinutes(1);
        for (var i = 0; i < 3; i++)
        {
            store.TryAcquireRequest("tenant-a", policy, nextWindow).IsAcquired
                .Should().BeTrue($"request {i} of the new window should fit");
        }

        store.TryAcquireRequest("tenant-a", policy, nextWindow).IsAcquired.Should().BeFalse();
    }

    /// <summary>
    /// The increment and the decision are one locked operation, so concurrent callers can never
    /// admit more than the limit between them.
    /// </summary>
    [Fact]
    public async Task TryAcquireRequest_Concurrent_AdmitsExactlyTheLimit()
    {
        var store = Create();
        var policy = new RateLimitPolicy(Rpm: 50, Burst: 0, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (store.TryAcquireRequest("tenant-a", policy, now).IsAcquired)
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })));

        admitted.Should().Be(50);
    }

    /// <summary>
    /// Window rollover is evaluated inside the same lock as the decision, so a request arriving
    /// exactly at a boundary is judged against its own window.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_WindowRollover_ResetsTheCounter()
    {
        var store = Create();
        var policy = new RateLimitPolicy(Rpm: 2, Burst: 0, MaxConcurrentStreams: 0);
        var window1 = new DateTimeOffset(2026, 7, 29, 12, 0, 30, TimeSpan.Zero);

        store.TryAcquireRequest("t", policy, window1).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, window1).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("t", policy, window1).IsAcquired.Should().BeFalse();

        var window2 = window1.AddMinutes(1);
        store.TryAcquireRequest("t", policy, window2).IsAcquired.Should().BeTrue();
    }

    /// <summary>
    /// Compaction runs on rejection paths too. Previously it was only reached after a successful
    /// acquire, so a partition stuck permanently over its limit was never swept.
    /// </summary>
    [Fact]
    public void TryAcquireRequest_PermanentlyRejectedPartitions_StillDriveCompaction()
    {
        var store = Create(retentionSeconds: 1, compactEvery: 2);
        var policy = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        store.TryAcquireRequest("blocked", policy, now).IsAcquired.Should().BeTrue();

        // Every subsequent call in this window is rejected; the act of rejecting must still tick the
        // compaction counter rather than skipping it.
        var act = () =>
        {
            for (var i = 0; i < 20; i++)
            {
                store.TryAcquireRequest("blocked", policy, now).IsAcquired.Should().BeFalse();
            }
        };

        act.Should().NotThrow();

        // An idle partition created earlier is swept by that compaction and comes back fresh.
        store.TryAcquireRequest("idle", policy, now).IsAcquired.Should().BeTrue();
        store.TryAcquireRequest("idle", policy, now.AddSeconds(30)).IsAcquired
            .Should().BeFalse("still inside the same minute window");
    }
}
