using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Services;

namespace Pol33.Api.DependencyInjection;

public static class GatewayApiServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayApi(this IServiceCollection services)
    {
        services.AddSingleton<GatewayProcessClock>();
        services.AddSingleton<ModelsApiService>();
        services.AddSingleton<GatewayHealthService>();
        services.AddSingleton<GatewayStatsService>();
        return services;
    }
}
