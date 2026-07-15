using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Hosting;
using Pol33.App.Cors;

namespace Pol33.App.DependencyInjection;

public static class GatewayCorsServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _ = configuration;
        _ = environment;

        services.AddCors();
        services.AddSingleton<ICorsPolicyProvider, GatewayCorsPolicyProvider>();
        services.AddHostedService<GatewayCorsStartupWarningHostedService>();

        return services;
    }
}
