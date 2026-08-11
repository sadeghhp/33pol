using Microsoft.Extensions.Options;
using Pol33.App.DependencyInjection;
using Pol33.Api.DependencyInjection;
using Pol33.Core.Configuration;
using Pol33.Core.Http;
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

        services.AddSingleton<IPostConfigureOptions<GatewayOptions>, GatewayCorsEnvironmentPostConfigure>();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidateOptions>();
        // Cross-section: the reservation TTL is only meaningful relative to the resilience timeouts.
        services.AddSingleton<IValidateOptions<BillingOptions>, BillingOptionsValidateOptions>();
        services.AddOptions<BillingOptions>().ValidateOnStart();
        services.AddGatewayCors(configuration, environment);
        services.AddGatewayOpenTelemetry();
        services.AddGatewayObservability();
        services.AddGatewayPersistence(configuration);
        services.AddGatewaySecurity(configuration);
        services.AddGatewayPolicy(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);
        services.AddGatewayStatsPersistence(configuration);
        services.AddGatewayConfigSnapshot(configuration);
        services.AddGatewayRegistry();
        services.AddGatewayApi();
        services.AddGatewayProxy();
        services.AddHttpClient(UpstreamHttpClientNames.Inference)
            .ConfigureHttpClient(client =>
            {
                // Deadlines are owned per request by InferenceHttpForwarder, which splits them into a
                // header phase and an idle-rearmed body phase and reports which one fired. A client
                // timeout here is a second, hidden deadline over the whole exchange: it would cap the
                // header allowance the forwarder widens for large-context requests, re-imposing the
                // very ceiling that made a working backend look dead to the circuit breaker.
                client.Timeout = Timeout.InfiniteTimeSpan;
            });

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
