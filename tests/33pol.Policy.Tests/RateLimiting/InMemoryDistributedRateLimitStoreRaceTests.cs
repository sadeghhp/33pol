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
    private static InMemoryDistributedRateLimitStore Create(int retentionSeconds = 3600) =>
        new(Options.Create(new RateLimitingOptions
        {
            InMemoryPartitionRetentionSeconds = retentionSeconds,
        }));

    /// <summary>
    /// The maintenance sweep now runs on its own thread, concurrently with traffic, so the eviction
    /// race it can lose is wider than it was when sweeping happened inline. A tenant that never
    /// exceeds its limit must still never see a rejection — every one would be a tombstoned state
    /// being handed out as if it were live, which used to fail acquisition outright.
    /// </summary>
    [Fact]
    public async Task TryAcquireStreamSlot_WhileTheSweeperRuns_NeverRejectsBelowTheLimit()
    {
        // Instant staleness maximises how much a concurrent sweep is willing to evict.
        var store = Create(retentionSeconds: 1);
        var policy = new RateLimitPolicy(Rpm: 100_000, Burst: 0, MaxConcurrentStreams: 4);

        var spuriousRejections = 0;
        using var sweeping = new CancellationTokenSource();

        var sweeper = Task.Run(() =>
        {
            while (!sweeping.IsCancellationRequested)
            {
                // A far-future timestamp: everything idle is stale, so every sweep evicts whatever
                // is not holding a slot at that instant.
                store.Compact(DateTimeOffset.UtcNow.AddHours(1));
            }
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                // One slot at a time per worker, so a distinct partition per worker keeps the
                // invariant strictly "one in flight, limit 4".
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

        await sweeping.CancelAsync();
        await sweeper;

        spuriousRejections.Should().Be(
            0,
            "no partition ever held more than one concurrent stream against a limit of 4");
    }

    [Fact]
    public void TryAcquireStreamSlot_AfterEvictionOfAnIdlePartition_StillAcquires()
    {
        var store = Create(retentionSeconds: 1);
        var policy = new RateLimitPolicy(Rpm: 100_000, Burst: 0, MaxConcurrentStreams: 2);

        store.TryAcquireStreamSlot("tenant-a", policy).IsAcquired.Should().BeTrue();
        store.ReleaseStreamSlot("tenant-a");

        // Sweep it away while idle, then use it again.
        store.Compact(DateTimeOffset.UtcNow.AddHours(1));

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
    /// A rejection touches the partition, so a caller stuck permanently over its limit is never the
    /// one a sweep evicts. Evicting it would hand it a fresh, full bucket — a limit bypass available
    /// to anyone willing to keep being refused.
    /// </summary>
    [Fact]
    public void Compact_WhileAPartitionIsBeingRejected_DoesNotEvictIt()
    {
        var store = Create(retentionSeconds: 60);
        var policy = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        store.TryAcquireRequest("blocked", policy, now).IsAcquired.Should().BeTrue();

        for (var i = 0; i < 20; i++)
        {
            store.TryAcquireRequest("blocked", policy, now).IsAcquired.Should().BeFalse();
            store.Compact(now);
        }

        store.TryAcquireRequest("blocked", policy, now).IsAcquired
            .Should().BeFalse("the partition kept its drained bucket across every sweep");
    }
}
