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

    [Fact]
    public void RecordUsageParseFailure_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordUsageParseFailure("m1");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordEstimatedUsage_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordEstimatedUsage("m1");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordUnsplitUsage_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordUnsplitUsage("m1");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordInferenceRouted_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordInferenceRouted("m1", "chat", isStreaming: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordForwardAttempt_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordForwardAttempt("m1", "success");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordModelResolve_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordModelResolve("hit");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordCircuitBreakerTransition_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordCircuitBreakerTransition("m1", "open");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordBulkheadRejection_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordBulkheadRejection("m1");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordBulkheadInflightChange_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordBulkheadInflightChange("m1", 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordBillingReconciliation_DoesNotThrow()
    {
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        var act = () => collector.RecordBillingReconciliation(2, 1.25);
        act.Should().NotThrow();
    }
}
