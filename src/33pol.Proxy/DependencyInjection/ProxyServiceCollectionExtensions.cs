using System.Net;
using System.Net.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Hosting;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Resilience;
using Pol33.Proxy.Tracking;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.DependencyInjection;

public static class ProxyServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayProxy(this IServiceCollection services)
    {
        services.AddHttpForwarder();
        services.AddSingleton<IGatewayDrainState, GatewayDrainState>();
        services.AddSingleton<ModelCircuitBreakerRegistry>();
        services.AddSingleton<BulkheadRegistry>();
        services.AddHostedService<GatewayShutdownHostedService>();
        services.AddSingleton(CreateHttpMessageInvoker);
        return services;
    }

    public static IApplicationBuilder UseInferenceResilience(this IApplicationBuilder app) =>
        app.UseMiddleware<InferenceResilienceMiddleware>();

    public static IApplicationBuilder UseGatewayRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<RateLimitMiddleware>();

    public static IApplicationBuilder UseGatewayQuotas(this IApplicationBuilder app) =>
        app.UseMiddleware<QuotaMiddleware>();

    public static IApplicationBuilder UseModelRouter(this IApplicationBuilder app) =>
        app.UseMiddleware<ModelRouterMiddleware>();

    private static HttpMessageInvoker CreateHttpMessageInvoker(IServiceProvider services)
    {
        var tlsOptions = services.GetRequiredService<IOptions<GatewayOptions>>().Value.Tls;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = tlsOptions.ValidateUpstreamCertificates
                    ? null
                    : static (_, _, _, _) => true,
            },
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
