using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Registry.Health;
using Pol33.Registry.Hosting;
using Pol33.Registry.Services;

namespace Pol33.Registry.DependencyInjection;

public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ModelRegistryService>();
        services.AddSingleton<IModelRegistry>(sp => sp.GetRequiredService<ModelRegistryService>());
        services.AddSingleton<BackendHealthStore>();
        services.AddSingleton<IBackendHealthStore>(sp => sp.GetRequiredService<BackendHealthStore>());
        services.AddHostedService<HealthCheckService>();
        services.AddHostedService<ModelRegistryInitializer>();
        services.AddSingleton<ConfigReloadService>();
        services.AddSingleton<IConfigReload>(sp => sp.GetRequiredService<ConfigReloadService>());
        services.AddHostedService(sp => sp.GetRequiredService<ConfigReloadService>());
        return services;
    }
}
