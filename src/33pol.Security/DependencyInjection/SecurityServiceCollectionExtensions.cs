using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Security.Authentication;
using Pol33.Security.Middleware;

namespace Pol33.Security.DependencyInjection;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddGatewaySecurity(this IServiceCollection services)
    {
        services.AddSingleton<IApiKeyValidator, ConfigApiKeyValidator>();
        return services;
    }

    public static IApplicationBuilder UseGatewayRequestId(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestIdMiddleware>();

    public static IApplicationBuilder UseGatewayApiKeyAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
}
