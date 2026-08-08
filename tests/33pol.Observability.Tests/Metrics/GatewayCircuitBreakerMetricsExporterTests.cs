using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Observability.Metrics;

namespace Pol33.Observability.Tests.Metrics;

public sealed class GatewayCircuitBreakerMetricsExporterTests
{
    [Fact]
    public void ObserveMeasurements_ReturnsStatePerModel()
    {
        var source = Substitute.For<ICircuitBreakerStateSource>();
        source.GetStates().Returns(
        [
            new CircuitBreakerModelState("gpt-a", 0),
            new CircuitBreakerModelState("gpt-b", 2),
        ]);

        var measurements = GatewayCircuitBreakerMetricsExporter.ObserveMeasurements(source).ToList();

        measurements.Should().HaveCount(2);
        measurements.Should().Contain(m => m.Value == 0);
        measurements.Should().Contain(m => m.Value == 2);
    }

    [Fact]
    public async Task StartAndStopAsync_CompleteSuccessfully()
    {
        var source = Substitute.For<ICircuitBreakerStateSource>();
        source.GetStates().Returns([]);
        var exporter = new GatewayCircuitBreakerMetricsExporter(source);

        await exporter.StartAsync(CancellationToken.None);
        await exporter.StopAsync(CancellationToken.None);
    }
}
