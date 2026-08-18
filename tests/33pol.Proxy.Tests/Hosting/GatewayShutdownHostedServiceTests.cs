using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Hosting;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Hosting;

public sealed class GatewayShutdownHostedServiceTests
{
    private static GatewayShutdownHostedService Create(int drainSeconds, GatewayDrainState? drain = null) =>
        new(
            drain ?? new GatewayDrainState(),
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { ShutdownDrainSeconds = drainSeconds },
            }),
            NullLogger<GatewayShutdownHostedService>.Instance);

    [Fact]
    public async Task StoppingAsync_BeginsDrain()
    {
        var drain = new GatewayDrainState();
        var service = Create(drainSeconds: 0, drain);

        await service.StartAsync(CancellationToken.None);
        await service.StoppingAsync(CancellationToken.None);

        drain.IsDraining.Should().BeTrue();
    }

    /// <summary>
    /// StopAsync runs after the server has already unbound, so it must not hold anything up — but
    /// raising the flag there is harmless and covers hosts that skip the lifecycle steps.
    /// </summary>
    [Fact]
    public async Task StopAsync_BeginsDrainWithoutHolding()
    {
        var drain = new GatewayDrainState();
        var service = Create(drainSeconds: 60, drain);

        var started = DateTimeOffset.UtcNow;
        await service.StopAsync(CancellationToken.None);

        drain.IsDraining.Should().BeTrue();
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The drain window is what gives load balancers time to deregister. It has to actually elapse —
    /// raising the flag and returning immediately, as an ApplicationStopping callback did, bought
    /// nothing because the server stopped accepting at the same instant.
    /// </summary>
    [Fact]
    public async Task StoppingAsync_HoldsForTheConfiguredDrainWindow()
    {
        var service = Create(drainSeconds: 1);
        await service.StartAsync(CancellationToken.None);

        var started = DateTimeOffset.UtcNow;
        await service.StoppingAsync(CancellationToken.None);

        (DateTimeOffset.UtcNow - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    }

    /// <summary>A host shutdown deadline must cut the drain short rather than block termination.</summary>
    [Fact]
    public async Task StoppingAsync_ReturnsWhenShutdownDeadlineElapses()
    {
        var service = Create(drainSeconds: 60);
        await service.StartAsync(CancellationToken.None);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = DateTimeOffset.UtcNow;
        await service.StoppingAsync(deadline.Token);

        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The whole point of using the stopping step: in a real host the drain must be complete before
    /// any hosted service's StopAsync runs — in the gateway that includes the server, which is
    /// registered last and therefore stopped first. A hosted service registered after this one stands
    /// in for Kestrel here and asserts the drain flag is already up when its StopAsync is reached.
    /// </summary>
    [Fact]
    public async Task Host_StopAsync_DrainCompletesBeforeAnyHostedServiceStops()
    {
        var drain = new GatewayDrainState();
        var probe = new ServerStandIn(drain);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IGatewayDrainState>(drain);
                services.AddSingleton(Options.Create(new GatewayOptions
                {
                    Resilience = new GatewayResilienceOptions { ShutdownDrainSeconds = 0 },
                }));
                services.AddHostedService<GatewayShutdownHostedService>();
                // Registered after, like GenericWebHostService, so it is stopped before.
                services.AddSingleton<IHostedService>(probe);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        probe.DrainingWhenStopped.Should().BeTrue();
    }

    private sealed class ServerStandIn(IGatewayDrainState drain) : IHostedService
    {
        public bool? DrainingWhenStopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            DrainingWhenStopped = drain.IsDraining;
            return Task.CompletedTask;
        }
    }
}
