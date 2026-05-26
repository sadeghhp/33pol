using Microsoft.Extensions.Options;
using Pol33.App.DependencyInjection;
using Pol33.Api.DependencyInjection;
using Pol33.Core.Configuration;
using Pol33.Billing.DependencyInjection;
using Pol33.Policy.DependencyInjection;
using Pol33.Proxy.DependencyInjection;
using Pol33.Persistence.DependencyInjection;
using Pol33.Observability.DependencyInjection;
using Pol33.OperatorConsole.DependencyInjection;
using Pol33.Registry.DependencyInjection;
using Pol33.Security.DependencyInjection;

namespace Pol33.App;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayCore(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidateOptions>();
        services.AddGatewayCors(configuration, environment);
        services.AddGatewayOpenTelemetry();
        services.AddGatewayObservability();
        services.AddGatewayPersistence(configuration);
        services.AddGatewaySecurity(configuration);
        services.AddGatewayPolicy(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);
        services.AddGatewayRegistry();
        services.AddGatewayApi();
        services.AddGatewayProxy();

        if (configuration.GetValue<bool>("Gateway:OperatorConsole:Enabled"))
        {
            services.AddOperatorConsole(configuration);
        }

        return services;
    }

    public static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }
}
