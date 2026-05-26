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

        using (tracker.BeginInferenceRequest("gpt-4o", isStreaming: true))
        {
            runtime.GetStats().ActiveStreams.Should().Be(1);
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
        scope.Dispose();
        scope.Dispose();

        runtime.GetStats().Total.Should().Be(1);
    }
}
