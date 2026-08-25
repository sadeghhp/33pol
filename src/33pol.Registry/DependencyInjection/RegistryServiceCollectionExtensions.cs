using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Providers;
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
        // The resolver enforces this policy at the point of use, so it must be present whenever the
        // registry is — not only when the admin API module happens to also be composed.
        services.TryAddSingleton(sp => UpstreamEnvVarPolicy.FromOptions(
            sp.GetRequiredService<IOptions<GatewayOptions>>().Value));
        services.AddSingleton<UpstreamBearerTokenResolver>();
        services.AddSingleton<IUpstreamBearerTokenResolver>(sp => sp.GetRequiredService<UpstreamBearerTokenResolver>());
        services.AddSingleton<ModelRegistryWriter>();
        services.AddSingleton<IModelRegistryWriter>(sp => sp.GetRequiredService<ModelRegistryWriter>());
        services.AddSingleton<BackendHealthStore>();
        services.AddSingleton<IBackendHealthStore>(sp => sp.GetRequiredService<BackendHealthStore>());
        services.AddHostedService<UpstreamSecretVerificationHostedService>();
        services.AddHostedService<HealthCheckService>();
        services.AddSingleton<ModelRegistryLoader>();
        services.AddHostedService<ModelRegistryLoaderHostedService>();
        services.AddHostedService<ModelRegistryRouteReconcileService>();
        services.AddSingleton<IConfigReload, ModelRegistryConfigReload>();
        return services;
    }
}
