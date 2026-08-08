using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Proxy.Hosting;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Hosting;

public sealed class GatewayShutdownHostedServiceTests
{
    private static GatewayShutdownHostedService Create(int drainSeconds) =>
        new(
            new GatewayDrainState(),
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { ShutdownDrainSeconds = drainSeconds },
            }),
            NullLogger<GatewayShutdownHostedService>.Instance);

    [Fact]
    public async Task StopAsync_BeginsDrain()
    {
        var drain = new GatewayDrainState();
        var service = new GatewayShutdownHostedService(
            drain,
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { ShutdownDrainSeconds = 0 },
            }),
            NullLogger<GatewayShutdownHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        drain.IsDraining.Should().BeTrue();
    }

    /// <summary>
    /// The drain window is what gives load balancers time to deregister. It has to actually elapse —
    /// raising the flag and returning immediately, as an ApplicationStopping callback did, bought
    /// nothing because the server stopped accepting at the same instant.
    /// </summary>
    [Fact]
    public async Task StopAsync_HoldsForTheConfiguredDrainWindow()
    {
        var service = Create(drainSeconds: 1);
        await service.StartAsync(CancellationToken.None);

        var started = DateTimeOffset.UtcNow;
        await service.StopAsync(CancellationToken.None);

        (DateTimeOffset.UtcNow - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    }

    /// <summary>A host shutdown deadline must cut the drain short rather than block termination.</summary>
    [Fact]
    public async Task StopAsync_ReturnsWhenShutdownDeadlineElapses()
    {
        var service = Create(drainSeconds: 60);
        await service.StartAsync(CancellationToken.None);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = DateTimeOffset.UtcNow;
        await service.StopAsync(deadline.Token);

        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
