using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class BulkheadRegistryStateTests
{
    [Fact]
    public async Task GetStates_WhileAcquired_ReportsInFlightAndCeilings()
    {
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 3, MaxQueuedForwardsPerModel = 2 },
            }),
            Substitute.For<IGatewayMetricsCollector>());

        registry.GetStates().Should().BeEmpty("no model has been admitted yet");

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        var second = await registry.TryAcquireAsync("m1", CancellationToken.None);

        var state = registry.GetStates().Should().ContainSingle().Subject;
        state.ModelId.Should().Be("m1");
        state.InFlight.Should().Be(2);
        state.Queued.Should().Be(0);
        state.MaxConcurrent.Should().Be(3);
        state.MaxQueued.Should().Be(2);

        first!.Dispose();
        second!.Dispose();
        registry.GetStates().Single().InFlight.Should().Be(0);
    }

    [Fact]
    public async Task GetStates_WithAWaiter_ReportsQueued()
    {
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions
                {
                    MaxConcurrentForwardsPerModel = 1,
                    MaxQueuedForwardsPerModel = 1,
                    BulkheadQueueTimeoutSeconds = 5,
                },
            }),
            Substitute.For<IGatewayMetricsCollector>());

        var held = await registry.TryAcquireAsync("m1", CancellationToken.None);
        var waiting = registry.TryAcquireAsync("m1", CancellationToken.None).AsTask();
        await Task.Delay(50);

        var state = registry.GetStates().Single();
        state.InFlight.Should().Be(1);
        state.Queued.Should().Be(1);

        held!.Dispose();
        (await waiting)!.Dispose();
    }
}
