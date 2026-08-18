using Microsoft.Extensions.Options;
using Pol33.App.DependencyInjection;
using Pol33.App.Metrics;
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
        services.AddGatewayMetricsAccess(configuration);
        services.AddGatewayOpenTelemetry();
        services.AddGatewayObservability();
        services.AddGatewayPersistence(configuration);
        services.AddGatewaySecurity(configuration);
        services.AddGatewayPolicy(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);
        services.AddGatewayStatsPersistence(configuration);
        services.AddGatewayErrorTracking(configuration);
        services.AddGatewayConfigSnapshot(configuration);
        services.AddGatewayRegistry();
        services.AddGatewayApi();
        services.AddGatewayProxy();
        services.AddHostedService<GatewayAdmissionLimitsStartupLogger>();
        services.AddHttpClient(UpstreamHttpClientNames.Inference)
            .ConfigureHttpClient(client =>
            {
                // Deadlines are owned per request by InferenceHttpForwarder, which splits them into a
                // header phase and an idle-rearmed body phase and reports which one fired. A client
                // timeout here is a second, hidden deadline over the whole exchange: it would cap the
                // header allowance the forwarder widens for large-context requests, re-imposing the
                // very ceiling that made a working backend look dead to the circuit breaker.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            // The connection pool is configured explicitly rather than left to the factory's
            // defaults. The factory otherwise rotates the whole handler — and with it every pooled
            // connection — every two minutes, so under steady load each rotation opens a fresh burst
            // of TCP connections to the model server; and it lets idle connections die after 60 s,
            // so bursty traffic pays a handshake per burst. Pinning the handler and letting
            // SocketsHttpHandler age connections itself is the pattern Microsoft documents for
            // long-lived clients. Nothing here caps concurrency: MaxConnectionsPerServer stays
            // unlimited unless the operator opts in, so the per-model bulkhead remains the only
            // admission control between clients and the GPU.
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var resilience = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Resilience;
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    // Upstream bodies must arrive uncompressed for usage capture to read them; the
                    // transformer already omits Accept-Encoding, this keeps the handler consistent.
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(resilience.UpstreamConnectTimeoutSeconds),
                    PooledConnectionLifetime = resilience.UpstreamPooledConnectionLifetimeSeconds > 0
                        ? TimeSpan.FromSeconds(resilience.UpstreamPooledConnectionLifetimeSeconds)
                        : Timeout.InfiniteTimeSpan,
                    PooledConnectionIdleTimeout =
                        TimeSpan.FromSeconds(resilience.UpstreamPooledConnectionIdleTimeoutSeconds),
                    MaxConnectionsPerServer = resilience.UpstreamMaxConnectionsPerServer > 0
                        ? resilience.UpstreamMaxConnectionsPerServer
                        : int.MaxValue,
                    // A single HTTP/2 connection multiplexes ~100 streams; a backend that speaks
                    // HTTP/2 would otherwise queue the 101st concurrent request behind the rest.
                    EnableMultipleHttp2Connections = true,
                };
                return handler;
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
