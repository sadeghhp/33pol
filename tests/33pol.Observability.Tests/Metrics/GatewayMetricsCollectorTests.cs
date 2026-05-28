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

    [Fact]
    public void RecordTokenUsage_DoesNotThrow()
    {
        var runtime = new GatewayRuntimeState();
        var collector = new GatewayMetricsCollector(runtime);

        var act = () => collector.RecordTokenUsage("m1", 3, 7);

        act.Should().NotThrow();
    }

    [Fact]
    public void RecordTimeToFirstToken_DoesNotThrow()
    {
        var runtime = new GatewayRuntimeState();
        var collector = new GatewayMetricsCollector(runtime);

        var act = () => collector.RecordTimeToFirstToken("m1", 0.12);

        act.Should().NotThrow();
    }
}
