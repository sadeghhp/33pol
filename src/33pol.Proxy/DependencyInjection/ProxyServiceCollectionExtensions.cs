using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Hosting;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Resilience;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.DependencyInjection;

public static class ProxyServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayProxy(this IServiceCollection services)
    {
        services.AddHttpForwarder();
        services.AddSingleton<IInferenceHttpForwarder, InferenceHttpForwarder>();
        services.AddSingleton<IGatewayDrainState, GatewayDrainState>();
        services.AddSingleton<ModelCircuitBreakerRegistry>();
        services.AddSingleton<ICircuitBreakerStateSource>(sp =>
            sp.GetRequiredService<ModelCircuitBreakerRegistry>());
        services.AddSingleton<BulkheadRegistry>();
        services.AddHostedService<GatewayShutdownHostedService>();
        return services;
    }

    public static IApplicationBuilder UseGatewayExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<GatewayExceptionHandlingMiddleware>();

    public static IApplicationBuilder UsePublicModelDetection(this IApplicationBuilder app) =>
        app.UseMiddleware<PublicModelDetectionMiddleware>();

    public static IApplicationBuilder UseInferenceResilience(this IApplicationBuilder app) =>
        app.UseMiddleware<InferenceResilienceMiddleware>();

    public static IApplicationBuilder UseGatewayRateLimiting(this IApplicationBuilder app) =>
        app.UseMiddleware<RateLimitMiddleware>();

    public static IApplicationBuilder UseGatewayQuotas(this IApplicationBuilder app) =>
        app.UseMiddleware<QuotaMiddleware>();

    public static IApplicationBuilder UseModelRouter(this IApplicationBuilder app) =>
        app.UseMiddleware<ModelRouterMiddleware>();

}
