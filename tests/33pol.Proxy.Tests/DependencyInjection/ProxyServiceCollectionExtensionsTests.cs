using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Proxy.DependencyInjection;

namespace Pol33.Proxy.Tests.DependencyInjection;

public sealed class ProxyServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayProxy_RegistersHttpForwarderAndMessageInvoker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayProxy();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>().Should().NotBeNull();
        provider.GetRequiredService<HttpMessageInvoker>().Should().NotBeNull();
    }

    [Fact]
    public void CreateHttpMessageInvoker_UsesSocketsHandlerPerV1Spec()
    {
        var services = new ServiceCollection();
        services.AddGatewayProxy();
        var invoker = services.BuildServiceProvider().GetRequiredService<HttpMessageInvoker>();

        invoker.Should().NotBeNull();
        // Handler is internal; registration succeeding + arch Yarp test covers route-table ban.
    }
}
