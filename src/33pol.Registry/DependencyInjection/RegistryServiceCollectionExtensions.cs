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
        services.AddSingleton<RegistryGate>();
        services.AddSingleton<ModelRegistryService>();
        services.AddSingleton<IModelRegistry>(sp => sp.GetRequiredService<ModelRegistryService>());
        services.AddSingleton<FileUpstreamSecretStore>();
        services.AddSingleton<IUpstreamSecretStore>(sp => sp.GetRequiredService<FileUpstreamSecretStore>());
        services.AddSingleton<UpstreamBearerTokenResolver>();
        services.AddSingleton<IUpstreamBearerTokenResolver>(sp => sp.GetRequiredService<UpstreamBearerTokenResolver>());
        services.AddSingleton<ModelRegistryWriter>();
        services.AddSingleton<IModelRegistryWriter>(sp => sp.GetRequiredService<ModelRegistryWriter>());
        services.AddSingleton<BackendHealthStore>();
        services.AddSingleton<IBackendHealthStore>(sp => sp.GetRequiredService<BackendHealthStore>());
        services.AddHostedService<HealthCheckService>();
        services.AddSingleton<ModelRegistryLoader>();
        services.AddHostedService<ModelRegistryLoaderHostedService>();
        services.AddHostedService<ModelRegistryRouteReconcileService>();
        services.AddSingleton<IConfigReload, ModelRegistryConfigReload>();
        return services;
    }
}
