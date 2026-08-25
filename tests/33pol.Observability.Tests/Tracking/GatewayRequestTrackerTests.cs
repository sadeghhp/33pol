using Pol33.Observability.Runtime;
using Pol33.Observability.Tracking;

namespace Pol33.Observability.Tests.Tracking;

public sealed class GatewayRequestTrackerTests
{
    [Fact]
    public void BeginInferenceRequest_OnDispose_RecordsCompletion()
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        using (var scope = tracker.BeginInferenceRequest("gpt-4o", isStreaming: true))
        {
            runtime.GetStats().ActiveStreams.Should().Be(1);
            scope.SetOutcome(true);
        }

        var (total, errors, avgMs, activeStreams, _, _) = runtime.GetStats();
        total.Should().Be(1);
        errors.Should().Be(0);
        avgMs.Should().BeGreaterThanOrEqualTo(0);
        activeStreams.Should().Be(0);
    }

    [Fact]
    public void BeginInferenceRequest_DoubleDispose_RecordsOnce()
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        var scope = tracker.BeginInferenceRequest("m1", isStreaming: false);
        scope.SetOutcome(true);
        scope.Dispose();
        scope.Dispose();

        runtime.GetStats().Total.Should().Be(1);
    }

    [Fact]
    public void SetOutcome_False_RecordsErrorInRuntimeState()
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        using (var scope = tracker.BeginInferenceRequest("gpt-4o", isStreaming: false))
        {
            scope.SetOutcome(false, "upstream_error");
        }

        runtime.GetStats().Errors.Should().Be(1);
        runtime.GetErrorsPerModel()["gpt-4o"].Should().Be(1);
    }

    [Theory]
    [InlineData("bulkhead_full", "bulkhead")]
    [InlineData("circuit_open", "circuit_open")]
    [InlineData("backend_unhealthy", "backend_unhealthy")]
    [InlineData("insufficient_scope", "grant_denied")]
    public void RecordRejectedRequest_MapsTheOutcomeToAWindowedReason(string outcome, string reason)
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        tracker.RecordRejectedRequest("m1", outcome);

        var window = runtime.Windows.GetWindow(TimeSpan.FromMinutes(5));
        window.Requests.Should().Be(1);
        window.Errors.Should().Be(1);
        window.RejectionsByReason.Should().ContainKey(reason).WhoseValue.Should().Be(1);
    }

    [Fact]
    public void RecordRejectedRequest_StreamConcurrency_CountsTheRequestButNotASecondReason()
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        tracker.RecordRejectedRequest("m1", "stream_concurrency");

        var window = runtime.Windows.GetWindow(TimeSpan.FromMinutes(5));
        window.Requests.Should().Be(1);
        window.RejectionsByReason.Should().BeEmpty("the router counts stream caps through RecordRateLimitRejection");
    }


    [Fact]
    public void BeginInferenceRequest_WithTenant_CountsTheTenantsRequestsForTheOverview()
    {
        var runtime = new GatewayRuntimeState();
        var tracker = new GatewayRequestTracker(runtime);

        using (tracker.BeginInferenceRequest("m1", isStreaming: false, tenantId: "tenant-a")) { }
        using (tracker.BeginInferenceRequest("m1", isStreaming: false, tenantId: "tenant-a")) { }
        using (tracker.BeginInferenceRequest("m1", isStreaming: false, tenantId: null)) { }

        var top = runtime.TenantRequests.Top(DateTimeOffset.UtcNow, 1440, 10);
        top.Should().ContainSingle(r => r.Key == "tenant-a" && r.Count == 2);
        runtime.GetStats().Total.Should().Be(3);
    }

}
