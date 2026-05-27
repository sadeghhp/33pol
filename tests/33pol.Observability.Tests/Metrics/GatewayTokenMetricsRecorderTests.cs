using System.Diagnostics.Metrics;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Metrics;

public sealed class GatewayTokenMetricsRecorderTests
{
    [Fact]
    public void RecordTokenUsage_EmitsInputOutputAndTotalDirections()
    {
        var measurements = new List<(long Value, string Direction)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == GatewayMeters.MeterName &&
                    instrument.Name == "gateway_tokens_total")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? direction = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "direction")
                {
                    direction = tag.Value?.ToString();
                }
            }

            measurements.Add((measurement, direction ?? string.Empty));
        });

        listener.Start();

        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        collector.RecordTokenUsage("gpt-test", promptTokens: 10, completionTokens: 5);

        listener.Dispose();

        measurements.Should().Contain(m => m.Direction == "input" && m.Value == 10);
        measurements.Should().Contain(m => m.Direction == "output" && m.Value == 5);
        measurements.Should().Contain(m => m.Direction == "total" && m.Value == 15);
    }
}
