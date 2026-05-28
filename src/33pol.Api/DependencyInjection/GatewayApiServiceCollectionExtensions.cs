using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Middleware;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;

namespace Pol33.Api.DependencyInjection;

public static class GatewayApiServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<IErrorResponseWriter, OpenAiErrorResponseWriter>();
        services.AddSingleton<GatewayProcessClock>();
        services.AddScoped<ModelsApiService>();
        services.AddSingleton<GatewayHealthService>();
        services.AddSingleton<GatewayReadinessService>();
        services.AddSingleton<GatewayStatsService>();
        services.AddSingleton<AdminModelProvisioningService>();
        services.AddSingleton<AdminModelTestService>();
        services.AddHttpClient<OpenAiCompatibleProviderModelsClient>()
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(30));
        return services;
    }

    public static IApplicationBuilder UseGatewayRequestId(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestIdMiddleware>();
}
