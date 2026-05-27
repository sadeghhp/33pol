using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Proxy.DependencyInjection;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.DependencyInjection;

public sealed class ProxyServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayProxy_RegistersHttpForwarderAndMessageInvoker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new GatewayOptions()));
        services.AddHttpClient(Core.Http.UpstreamHttpClientNames.Inference);
        services.AddGatewayProxy();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>().Should().NotBeNull();
        provider.GetRequiredService<IInferenceHttpForwarder>().Should().NotBeNull();
    }
}
