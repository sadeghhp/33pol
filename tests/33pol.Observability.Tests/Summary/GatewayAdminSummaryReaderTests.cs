using Pol33.Observability.Runtime;
using Pol33.Observability.Summary;

namespace Pol33.Observability.Tests.Summary;

public sealed class GatewayAdminSummaryReaderTests
{
    [Fact]
    public void GetSnapshot_AfterRequests_ReturnsAggregates()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("gpt-4o", isStreaming: false);
        runtime.RecordRequestComplete("gpt-4o", success: true, durationMs: 100, wasStreaming: false);
        runtime.RecordRateLimitRejection();
        runtime.RecordQuotaRejection();

        var reader = new GatewayAdminSummaryReader(runtime);
        var snapshot = reader.GetSnapshot();

        snapshot.TotalInferenceRequests.Should().Be(1);
        snapshot.AverageLatencyMs.Should().Be(100);
        snapshot.RateLimitRejections.Should().Be(1);
        snapshot.QuotaRejections.Should().Be(1);
        snapshot.Uptime.Should().NotBeNullOrEmpty();
        snapshot.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        snapshot.RequestsPerModel.Should().ContainKey("gpt-4o");
        snapshot.ActiveRequests.Should().Be(0);
        snapshot.ActiveRequestsPerModel.Should().BeEmpty();
    }

    /// <summary>
    /// The case the console could not show before: a non-streaming request that has started and not
    /// yet finished has to be visible as in-flight, and must not be counted as an active stream.
    /// </summary>
    [Fact]
    public void GetSnapshot_NonStreamingRequestInProgress_ReportsActiveRequestNotActiveStream()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("gpt-4o", isStreaming: false);

        var snapshot = new GatewayAdminSummaryReader(runtime).GetSnapshot();

        snapshot.ActiveRequests.Should().Be(1);
        snapshot.ActiveRequestsPerModel["gpt-4o"].Should().Be(1);
        snapshot.ActiveStreams.Should().Be(0);
        snapshot.TotalInferenceRequests.Should().Be(0);
    }

    [Fact]
    public void GetSnapshot_StreamInProgress_CountsTowardBothActiveGauges()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("gpt-4o", isStreaming: true);

        var snapshot = new GatewayAdminSummaryReader(runtime).GetSnapshot();

        snapshot.ActiveRequests.Should().Be(1);
        snapshot.ActiveStreams.Should().Be(1);
    }

    [Fact]
    public void GetSnapshot_RejectedAtAdmission_CountsAsRequestAndError()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestRejected("gpt-4o");

        var snapshot = new GatewayAdminSummaryReader(runtime).GetSnapshot();

        snapshot.TotalInferenceRequests.Should().Be(1);
        snapshot.TotalErrors.Should().Be(1);
        snapshot.ErrorsPerModel["gpt-4o"].Should().Be(1);
        // Admission takes microseconds; contributing it would drag the mean toward zero.
        snapshot.AverageLatencyMs.Should().Be(0);
    }

    [Fact]
    public void GetSnapshot_IncludesWindowsAndSeries()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("gpt-4o", isStreaming: true);
        runtime.RecordTimeToFirstToken("gpt-4o", 120);
        runtime.RecordRequestComplete("gpt-4o", success: true, durationMs: 400, wasStreaming: true);

        var snapshot = new GatewayAdminSummaryReader(runtime).GetSnapshot();

        snapshot.Windows.Should().NotBeNull();
        snapshot.Windows!.Select(w => w.Window).Should().Equal("1m", "5m", "1h", "24h");
        var fiveMinutes = snapshot.Windows.Single(w => w.Window == "5m");
        fiveMinutes.Requests.Should().Be(1);
        fiveMinutes.TtftSamples.Should().Be(1);
        fiveMinutes.PerModel.Should().ContainSingle(m => m.ModelId == "gpt-4o");
        snapshot.Series.Should().NotBeNull();
        snapshot.Series!.Points.Should().HaveCount(60);
        snapshot.Series.Points[^1].Requests.Should().Be(1);
    }

    [Fact]
    public void GetSnapshot_SameVersionAndSecond_ReturnsTheMemoisedInstance()
    {
        var runtime = new GatewayRuntimeState();
        var clock = new FrozenTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var reader = new GatewayAdminSummaryReader(runtime, clock);

        var first = reader.GetSnapshot();
        var second = reader.GetSnapshot();
        runtime.RecordRateLimitRejection();
        var third = reader.GetSnapshot();

        second.Should().BeSameAs(first);
        third.Should().NotBeSameAs(first);
        third.RateLimitRejections.Should().Be(1);
    }

    [Fact]
    public void GetSnapshot_WindowsDisabled_OmitsTheSections()
    {
        var runtime = new GatewayRuntimeState(new RollingWindowStats { Enabled = false });

        var snapshot = new GatewayAdminSummaryReader(runtime).GetSnapshot();

        snapshot.Windows.Should().BeNull();
        snapshot.Series.Should().BeNull();
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
