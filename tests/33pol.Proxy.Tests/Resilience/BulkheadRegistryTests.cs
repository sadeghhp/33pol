using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class BulkheadRegistryTests
{
    [Fact]
    public async Task TryAcquireAsync_WithinLimit_ReturnsReleasableLease()
    {
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 2 },
            }),
            Substitute.For<IGatewayMetricsCollector>());

        var lease = await registry.TryAcquireAsync("m1", CancellationToken.None);
        lease.Should().NotBeNull();
        lease!.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_AtCapacity_ReturnsNull()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 1 },
            }),
            metrics);

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await registry.TryAcquireAsync("m1", CancellationToken.None);
        second.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m1");

        first!.Dispose();
        var third = await registry.TryAcquireAsync("m1", CancellationToken.None);
        third.Should().NotBeNull();
        third!.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTrackedModelLimitReached_RejectsNewModel()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions
                {
                    MaxConcurrentForwardsPerModel = 1,
                    MaxTrackedResilienceModels = 1,
                },
            }),
            metrics);

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var secondModel = await registry.TryAcquireAsync("m2", CancellationToken.None);
        secondModel.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m2");

        first!.Dispose();
    }
}
