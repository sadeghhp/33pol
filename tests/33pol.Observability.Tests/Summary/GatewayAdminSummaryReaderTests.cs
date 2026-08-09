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
}
