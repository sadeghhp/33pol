using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Proxy.Hosting;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Hosting;

public sealed class GatewayShutdownHostedServiceTests
{
    [Fact]
    public async Task StopAsync_BeginsDrain()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var drain = new GatewayDrainState();
        var service = new GatewayShutdownHostedService(lifetime, drain);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        drain.IsDraining.Should().BeTrue();
    }
}
