using Microsoft.Extensions.Hosting;

namespace Pol33.Observability.Runtime;

/// <summary>
/// Samples the in-flight gauge into the windowed statistics every few seconds (the series keeps the
/// per-minute peak) and nudges the runtime version so an idle Overview still sees its 1-minute
/// window drain: without traffic nothing else would move the version, and the live stream only
/// pushes a frame when it does.
/// </summary>
public sealed class GatewayOverviewSamplerHostedService(GatewayRuntimeState runtimeState, TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                runtimeState.SampleInFlight();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
