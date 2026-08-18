using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.App.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Integration.Tests.Configuration;

/// <summary>
/// The initial database load is bounded. Unbounded, an unreachable database at boot meant a process
/// that never bound its port and never exited — invisible to every restart policy.
/// </summary>
public sealed class GatewayConfigSnapshotStartupTests
{
    [Fact]
    public async Task StartAsync_DatabaseNeverAnswers_FailsWithinTheStartupBudget()
    {
        var store = Substitute.For<IGatewayConfigStore>();
        store.LoadSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns<GatewayConfigSnapshot>(_ => throw new InvalidOperationException("database unreachable"));

        var service = CreateService(store, initialLoadTimeoutSeconds: 1);

        var stopwatch = Stopwatch.StartNew();
        var act = () => service.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<GatewayConfigStartupException>();
        thrown.Which.Message.Should().Contain("Gateway:ConfigSnapshot:InitialLoadTimeoutSeconds");
        thrown.Which.Message.Should().Contain("ConnectionStrings:GatewayDb");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        store.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGatewayConfigStore.LoadSnapshotAsync)).Should().BeGreaterThanOrEqualTo(2);
        service.HasLoadedOnce.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_DatabaseRecoversInsideTheBudget_Starts()
    {
        var calls = 0;
        var store = Substitute.For<IGatewayConfigStore>();
        store.LoadSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new InvalidOperationException("still booting");
                }

                return Task.FromResult(GatewayConfigSnapshot.Defaults with { Version = 7 });
            });
        store.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(7L);

        var service = CreateService(store, initialLoadTimeoutSeconds: 30);

        await service.StartAsync(CancellationToken.None);
        try
        {
            service.HasLoadedOnce.Should().BeTrue();
            calls.Should().Be(3);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_HostStopRequested_StopsRetryingWithoutTheStartupError()
    {
        var store = Substitute.For<IGatewayConfigStore>();
        store.LoadSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns<GatewayConfigSnapshot>(_ => throw new InvalidOperationException("database unreachable"));

        var service = CreateService(store, initialLoadTimeoutSeconds: 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => service.StartAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static GatewayConfigSnapshotService CreateService(IGatewayConfigStore store, int initialLoadTimeoutSeconds)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();

        return new GatewayConfigSnapshotService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GatewayConfigState(GatewayConfigSnapshot.Defaults),
            Options.Create(new GatewayConfigSnapshotOptions { InitialLoadMaxBackoffSeconds = 1, ReloadIntervalSeconds = 60 }),
            Options.Create(new GatewayConfigSnapshotStartupOptions { InitialLoadTimeoutSeconds = initialLoadTimeoutSeconds }),
            NullLogger<GatewayConfigSnapshotService>.Instance);
    }
}
