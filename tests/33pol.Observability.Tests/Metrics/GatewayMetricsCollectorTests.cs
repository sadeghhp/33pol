using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Metrics;

public sealed class GatewayMetricsCollectorTests
{
    [Fact]
    public void RecordRateLimitRejection_IncrementsRuntimeCounter()
    {
        var runtime = new GatewayRuntimeState();
        var collector = new GatewayMetricsCollector(runtime);

        collector.RecordRateLimitRejection("rpm");

        runtime.GetStats().RateLimit.Should().Be(1);
    }

    [Fact]
    public void RecordQuotaRejection_IncrementsRuntimeCounter()
    {
        var runtime = new GatewayRuntimeState();
        var collector = new GatewayMetricsCollector(runtime);

        collector.RecordQuotaRejection();

        runtime.GetStats().Quota.Should().Be(1);
    }
}
