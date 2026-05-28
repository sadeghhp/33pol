using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
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
        services.AddSingleton<IGatewayMetricsCollector, NoOpGatewayMetricsCollector>();
        services.AddHttpClient(Core.Http.UpstreamHttpClientNames.Inference);
        services.AddGatewayProxy();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>().Should().NotBeNull();
        provider.GetRequiredService<IInferenceHttpForwarder>().Should().NotBeNull();
    }

    private sealed class NoOpGatewayMetricsCollector : IGatewayMetricsCollector
    {
        public void RecordRateLimitRejection(string reason) { }
        public void RecordQuotaRejection() { }
        public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) { }
        public void RecordUsageParseFailure(string modelId) { }
        public void RecordInferenceRouted(string modelId, string route, bool isStreaming) { }
        public void RecordForwardAttempt(string modelId, string outcome) { }
        public void RecordModelResolve(string result) { }
        public void RecordCircuitBreakerTransition(string modelId, string toState) { }
        public void RecordBulkheadRejection(string modelId) { }
        public void RecordBulkheadInflightChange(string modelId, int delta) { }
        public void RecordTimeToFirstToken(string modelId, double seconds) { }
    }
}
