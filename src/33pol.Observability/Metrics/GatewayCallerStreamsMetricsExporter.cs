using Microsoft.Extensions.Hosting;

namespace Pol33.Observability.Metrics;

/// <summary>Publishes <see cref="CallerOpenStreams"/> as two gauges labelled with the caller.</summary>
/// <remarks>
/// Observable gauges, read at scrape time, so the request path pays one interlocked add per stream
/// and nothing per scrape series. The series count is the number of callers that have streamed,
/// bounded by <see cref="MetricCallerBudget"/>.
/// </remarks>
public sealed class GatewayCallerStreamsMetricsExporter(CallerOpenStreams openStreams) : IHostedService
{
    public const string OpenStreamsName = "gateway_key_open_streams";

    public const string PeakOpenStreamsName = "gateway_key_open_streams_peak";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayMeters.Meter.CreateObservableGauge(
            OpenStreamsName,
            openStreams.ObserveOpen,
            description: "Streaming responses open right now, per caller");

        GatewayMeters.Meter.CreateObservableGauge(
            PeakOpenStreamsName,
            openStreams.ObservePeak,
            description: "Most streaming responses a caller has had open at once since the gateway started");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
