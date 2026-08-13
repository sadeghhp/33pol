using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

public sealed class GatewayRuntimeStateTests
{
    [Fact]
    public void EnqueueRecent_ExceedsMax_TrimsOldest()
    {
        var runtime = new GatewayRuntimeState { MaxRecentRequests = 2 };
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r2",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r3",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });

        var recent = runtime.GetRecent(10);
        recent.Should().HaveCount(2);
        recent[0].RequestId.Should().Be("r3");
        recent[1].RequestId.Should().Be("r2");
    }

    [Fact]
    public void EnqueueRecent_PreservesErrorCode()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 502,
            ErrorCode = "upstream_error",
        });

        runtime.GetRecent(1).Single().ErrorCode.Should().Be("upstream_error");
    }

    /// <summary>
    /// The live feed has to show a request while it runs. Before in-flight entries existed the feed
    /// was written only at completion, so an inference in progress left it empty.
    /// </summary>
    [Fact]
    public void BeginInFlight_SurfacesTheRequestBeforeItCompletes()
    {
        var runtime = new GatewayRuntimeState();
        runtime.BeginInFlight(InFlight("r1", startedSecondsAgo: 3));

        var entry = runtime.GetRecent(10).Single();
        entry.RequestId.Should().Be("r1");
        entry.IsInFlight.Should().BeTrue();
        entry.StatusCode.Should().Be(0);
        // Restamped at read time, which is what makes the timer advance across dashboard polls.
        entry.DurationMs.Should().BeGreaterThan(2_000);
    }

    [Fact]
    public void EnqueueRecent_SupersedesTheInFlightEntryForTheSameRequest()
    {
        var runtime = new GatewayRuntimeState();
        runtime.BeginInFlight(InFlight("r1", startedSecondsAgo: 1));
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
            DurationMs = 1_234,
        });

        var recent = runtime.GetRecent(10);
        recent.Should().HaveCount(1, "the finished row replaces the in-flight one rather than doubling it");
        recent[0].IsInFlight.Should().BeFalse();
        recent[0].DurationMs.Should().Be(1_234);
    }

    [Fact]
    public void CompleteInFlight_RemovesAnAbandonedRequest()
    {
        var runtime = new GatewayRuntimeState();
        runtime.BeginInFlight(InFlight("r1", startedSecondsAgo: 1));
        runtime.CompleteInFlight("r1");
        runtime.CompleteInFlight("r1"); // idempotent

        runtime.GetRecent(10).Should().BeEmpty();
    }

    [Fact]
    public void GetRecent_OrdersInFlightAheadOfCompleted()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "done",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });
        runtime.BeginInFlight(InFlight("running", startedSecondsAgo: 1));

        runtime.GetRecent(10).Select(e => e.RequestId).Should().Equal("running", "done");
    }

    [Fact]
    public void BeginInFlight_PastTheTrackedCap_DoesNotGrowUnbounded()
    {
        var runtime = new GatewayRuntimeState { MaxInFlightTracked = 2 };
        runtime.BeginInFlight(InFlight("r1", startedSecondsAgo: 1));
        runtime.BeginInFlight(InFlight("r2", startedSecondsAgo: 1));
        runtime.BeginInFlight(InFlight("r3", startedSecondsAgo: 1));

        runtime.GetRecent(10).Should().HaveCount(2);
    }

    [Fact]
    public void RecordRequestStart_TracksActiveRequestsPerModelAndReleasesThemOnCompletion()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("m1", isStreaming: false);
        runtime.RecordRequestStart("m1", isStreaming: false);
        runtime.RecordRequestStart("m2", isStreaming: true);

        runtime.GetActiveRequests().Should().Be(3);
        runtime.GetActiveRequestsPerModel()["m1"].Should().Be(2);
        runtime.GetStats().ActiveStreams.Should().Be(1);

        runtime.RecordRequestComplete("m1", success: true, durationMs: 5, wasStreaming: false);
        runtime.RecordRequestComplete("m1", success: true, durationMs: 5, wasStreaming: false);

        runtime.GetActiveRequests().Should().Be(1);
        // Dropped rather than left at zero: the breakdown answers "what is running right now".
        runtime.GetActiveRequestsPerModel().Should().NotContainKey("m1");
    }

    [Fact]
    public void RecordRequestRejected_CountsAsRequestAndErrorWithoutLatency()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestRejected("m1");

        var (total, errors, avgMs, _, _, _) = runtime.GetStats();
        total.Should().Be(1);
        errors.Should().Be(1);
        avgMs.Should().Be(0);
        runtime.GetErrorsPerModel()["m1"].Should().Be(1);
        runtime.GetRequestsPerModel()["m1"].Should().Be(1);
    }

    /// <summary>In-flight entries are transient process state and must never reach the durable snapshot.</summary>
    [Fact]
    public void Export_OmitsInFlightEntries()
    {
        var runtime = new GatewayRuntimeState();
        runtime.BeginInFlight(InFlight("running", startedSecondsAgo: 1));

        runtime.Export().Recent.Should().BeEmpty();
    }

    [Fact]
    public void ResetErrors_ZeroesErrorCountersAndDropsFailedFeedRows()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestComplete("m1", success: true, durationMs: 100, wasStreaming: false);
        runtime.RecordRequestComplete("m1", success: false, durationMs: 200, wasStreaming: false);
        runtime.EnqueueRecent(Completed("ok", statusCode: 200, errorCode: null));
        runtime.EnqueueRecent(Completed("bad", statusCode: 502, errorCode: "upstream_error"));

        var (clearedTotal, removedRows) = runtime.ResetErrors();

        clearedTotal.Should().Be(1);
        removedRows.Should().Be(1);
        runtime.GetStats().Errors.Should().Be(0);
        runtime.GetErrorsPerModel().Should().BeEmpty();
        runtime.GetRecent(50).Select(r => r.RequestId).Should().Equal("ok");
    }

    /// <summary>
    /// Clearing errors must not silently rewrite the throughput and latency history alongside them.
    /// </summary>
    [Fact]
    public void ResetErrors_LeavesRequestAndLatencyCountersUntouched()
    {
        var runtime = new GatewayRuntimeState();
        var startedAt = runtime.StartedUtc;
        runtime.RecordRequestComplete("m1", success: true, durationMs: 100, wasStreaming: false);
        runtime.RecordRequestComplete("m1", success: false, durationMs: 300, wasStreaming: false);
        runtime.BeginInFlight(InFlight("running", startedSecondsAgo: 1));

        runtime.ResetErrors();

        var stats = runtime.GetStats();
        stats.Total.Should().Be(2);
        stats.AvgMs.Should().Be(200);
        runtime.GetRequestsPerModel()["m1"].Should().Be(2);
        runtime.StartedUtc.Should().Be(startedAt);
        runtime.GetRecent(50).Should().Contain(r => r.RequestId == "running");
    }

    [Fact]
    public void ResetErrors_PreservesFeedOrdering()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Completed("first", statusCode: 200, errorCode: null));
        runtime.EnqueueRecent(Completed("bad", statusCode: 500, errorCode: "upstream_error"));
        runtime.EnqueueRecent(Completed("second", statusCode: 200, errorCode: null));

        runtime.ResetErrors();

        // GetRecent reads newest-first, so the surviving rows must come back in reverse insertion order.
        runtime.GetRecent(50).Select(r => r.RequestId).Should().Equal("second", "first");
    }

    [Fact]
    public void ResetAll_ZeroesEveryLifetimeCounterAndEmptiesTheFeed()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestComplete("m1", success: true, durationMs: 100, wasStreaming: false);
        runtime.RecordRequestComplete("m1", success: false, durationMs: 300, wasStreaming: false);
        runtime.RecordRateLimitRejection();
        runtime.RecordQuotaRejection();
        runtime.EnqueueRecent(Completed("ok", statusCode: 200, errorCode: null));

        runtime.ResetAll();

        var stats = runtime.GetStats();
        stats.Total.Should().Be(0);
        stats.Errors.Should().Be(0);
        stats.AvgMs.Should().Be(0);
        stats.RateLimit.Should().Be(0);
        stats.Quota.Should().Be(0);
        runtime.GetRequestsPerModel().Should().BeEmpty();
        runtime.GetErrorsPerModel().Should().BeEmpty();
        runtime.GetRecent(50).Should().BeEmpty();
    }

    /// <summary>
    /// Active counts describe work in flight, not history. Zeroing them would leave the gauges
    /// permanently negative once those requests completed and decremented past zero.
    /// </summary>
    [Fact]
    public void ResetAll_LeavesLiveActivityAndUptimeAlone()
    {
        var runtime = new GatewayRuntimeState();
        var startedAt = runtime.StartedUtc;
        runtime.RecordRequestStart("m1", isStreaming: true);
        runtime.BeginInFlight(InFlight("running", startedSecondsAgo: 1));

        runtime.ResetAll();

        runtime.GetActiveRequests().Should().Be(1);
        runtime.GetStats().ActiveStreams.Should().Be(1);
        runtime.StartedUtc.Should().Be(startedAt);
        runtime.GetRecent(50).Should().Contain(r => r.RequestId == "running");
    }

    /// <summary>
    /// The export is what gets persisted, so an emptied runtime must produce an empty snapshot —
    /// otherwise a clear is undone by the next periodic flush.
    /// </summary>
    [Fact]
    public void ResetAll_ProducesAnEmptyExport()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestComplete("m1", success: false, durationMs: 300, wasStreaming: false);
        runtime.EnqueueRecent(Completed("ok", statusCode: 200, errorCode: null));

        runtime.ResetAll();

        var snapshot = runtime.Export();
        snapshot.TotalRequests.Should().Be(0);
        snapshot.TotalErrors.Should().Be(0);
        snapshot.TotalLatencyMs.Should().Be(0);
        snapshot.RequestsPerModel.Should().BeEmpty();
        snapshot.ErrorsPerModel.Should().BeEmpty();
        snapshot.Recent.Should().BeEmpty();
    }

    private static RecentRequestEntry Completed(string requestId, int statusCode, string? errorCode) => new()
    {
        RequestId = requestId,
        Method = "POST",
        Path = "/v1/chat/completions",
        ModelId = "m1",
        StatusCode = statusCode,
        ErrorCode = errorCode,
        DurationMs = 10,
        TimestampUtc = DateTimeOffset.UtcNow,
    };

    private static RecentRequestEntry InFlight(string requestId, int startedSecondsAgo) => new()
    {
        RequestId = requestId,
        Method = "POST",
        Path = "/v1/chat/completions",
        ModelId = "m1",
        StatusCode = 0,
        TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-startedSecondsAgo),
        IsInFlight = true,
    };
}
