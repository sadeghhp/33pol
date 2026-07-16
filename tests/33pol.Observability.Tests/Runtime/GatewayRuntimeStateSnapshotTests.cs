using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

public sealed class GatewayRuntimeStateSnapshotTests
{
    private static RecentRequestEntry Entry(string id, DateTimeOffset timestamp) => new()
    {
        RequestId = id,
        Method = "POST",
        Path = "/v1/chat/completions",
        ModelId = "m1",
        StatusCode = 200,
        DurationMs = 12,
        TimestampUtc = timestamp,
    };

    [Fact]
    public void Export_CapturesCountersLatencyAndRecentFeed()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestComplete("m1", success: true, durationMs: 100, wasStreaming: false);
        runtime.RecordRequestComplete("m1", success: false, durationMs: 300, wasStreaming: false);
        runtime.RecordRequestComplete("m2", success: true, durationMs: 200, wasStreaming: false);
        runtime.RecordRateLimitRejection();
        runtime.RecordQuotaRejection();
        runtime.RecordQuotaRejection();
        runtime.EnqueueRecent(Entry("r1", DateTimeOffset.UtcNow));

        var snapshot = runtime.Export();

        snapshot.TotalRequests.Should().Be(3);
        snapshot.TotalErrors.Should().Be(1);
        snapshot.TotalLatencyMs.Should().Be(600);
        snapshot.RateLimitRejections.Should().Be(1);
        snapshot.QuotaRejections.Should().Be(2);
        snapshot.RequestsPerModel["m1"].Should().Be(2);
        snapshot.RequestsPerModel["m2"].Should().Be(1);
        snapshot.ErrorsPerModel["m1"].Should().Be(1);
        snapshot.Recent.Should().ContainSingle(e => e.RequestId == "r1");
    }

    [Fact]
    public void Hydrate_SeedsCountersSoAverageLatencyAndTotalsAreRestored()
    {
        var snapshot = new GatewayRuntimeSnapshot
        {
            TotalRequests = 10,
            TotalErrors = 2,
            TotalLatencyMs = 1000,
            RateLimitRejections = 3,
            QuotaRejections = 4,
            RequestsPerModel = new Dictionary<string, long> { ["m1"] = 7, ["m2"] = 3 },
            ErrorsPerModel = new Dictionary<string, long> { ["m1"] = 2 },
        };

        var runtime = new GatewayRuntimeState();
        runtime.Hydrate(snapshot);

        var (total, errors, avgMs, activeStreams, rateLimit, quota) = runtime.GetStats();
        total.Should().Be(10);
        errors.Should().Be(2);
        avgMs.Should().Be(100); // 1000ms / 10 requests
        activeStreams.Should().Be(0);
        rateLimit.Should().Be(3);
        quota.Should().Be(4);
        runtime.GetRequestsPerModel()["m1"].Should().Be(7);
        runtime.GetErrorsPerModel()["m1"].Should().Be(2);
    }

    [Fact]
    public void Hydrate_ThenRecord_ContinuesFromSeededTotalsWithoutDoubleCounting()
    {
        var runtime = new GatewayRuntimeState();
        runtime.Hydrate(new GatewayRuntimeSnapshot
        {
            TotalRequests = 5,
            TotalLatencyMs = 500,
            RequestsPerModel = new Dictionary<string, long> { ["m1"] = 5 },
        });

        runtime.RecordRequestComplete("m1", success: true, durationMs: 100, wasStreaming: false);

        var (total, _, _, _, _, _) = runtime.GetStats();
        total.Should().Be(6); // 5 seeded + 1 new, not reset and not doubled
        runtime.GetRequestsPerModel()["m1"].Should().Be(6);
    }

    [Fact]
    public void ExportThenHydrate_RoundTripsRecentFeedInChronologicalOrder()
    {
        var source = new GatewayRuntimeState();
        var t0 = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        source.EnqueueRecent(Entry("r1", t0));
        source.EnqueueRecent(Entry("r2", t0.AddSeconds(1)));
        source.EnqueueRecent(Entry("r3", t0.AddSeconds(2)));

        var restored = new GatewayRuntimeState();
        restored.Hydrate(source.Export());

        // GetRecent returns newest-first; ordering must be preserved across the round-trip.
        var recent = restored.GetRecent(10);
        recent.Select(e => e.RequestId).Should().ContainInOrder("r3", "r2", "r1");
    }

    [Fact]
    public void Hydrate_DoesNotRestoreUptime()
    {
        var runtime = new GatewayRuntimeState();
        var startedBefore = runtime.StartedUtc;

        runtime.Hydrate(new GatewayRuntimeSnapshot { TotalRequests = 100 });

        // StartedUtc drives uptime and must stay process-local (uptime resets on restart).
        runtime.StartedUtc.Should().Be(startedBefore);
    }
}
