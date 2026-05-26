using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class BulkheadRegistryTests
{
    [Fact]
    public async Task TryAcquireAsync_WithinLimit_ReturnsReleasableLease()
    {
        var registry = new BulkheadRegistry(Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 2 },
        }));

        var lease = await registry.TryAcquireAsync("m1", CancellationToken.None);
        lease.Should().NotBeNull();
        lease!.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_AtCapacity_ReturnsNull()
    {
        var registry = new BulkheadRegistry(Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 1 },
        }));

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await registry.TryAcquireAsync("m1", CancellationToken.None);
        second.Should().BeNull();

        first!.Dispose();
        var third = await registry.TryAcquireAsync("m1", CancellationToken.None);
        third.Should().NotBeNull();
        third!.Dispose();
    }
}
