using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Tracking;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.DependencyInjection;

public static class ProxyServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayProxy(this IServiceCollection services)
    {
        services.AddHttpForwarder();
        services.AddSingleton<IRequestTracker, RequestTracker>();
        services.AddSingleton(CreateHttpMessageInvoker);
        return services;
    }

    public static IApplicationBuilder UseModelRouter(this IApplicationBuilder app) =>
        app.UseMiddleware<ModelRouterMiddleware>();

    private static HttpMessageInvoker CreateHttpMessageInvoker(IServiceProvider _)
    {
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
        };

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
