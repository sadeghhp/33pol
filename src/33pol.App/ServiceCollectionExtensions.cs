using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.DependencyInjection;

namespace Pol33.App;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCore(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidateOptions>();
        services.AddGatewayRegistry();

        return services;
    }

    public static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }
}
