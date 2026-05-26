using Pol33.Observability.Runtime;
using Pol33.Observability.Summary;

namespace Pol33.Observability.Tests.Summary;

public sealed class GatewayAdminSummaryReaderTests
{
    [Fact]
    public void GetSnapshot_AfterRequests_ReturnsAggregates()
    {
        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart(isStreaming: false);
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
    }
}
