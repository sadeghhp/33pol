using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Observability.ControlPlane;
using Pol33.Observability.Diagnostics;
using Pol33.Observability.Metrics;
using Pol33.Observability.RecentRequests;
using Pol33.Observability.Runtime;
using Pol33.Observability.Summary;
using Pol33.Observability.Tracking;
using Pol33.Observability.Usage;

namespace Pol33.Observability.DependencyInjection;

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayObservability(this IServiceCollection services)
    {
        services.AddSingleton<GatewayRuntimeState>();
        services.AddSingleton<IGatewayMetricsCollector, GatewayMetricsCollector>();
        services.AddSingleton<IRequestTracker, GatewayRequestTracker>();
        services.AddSingleton<IRecentRequestStore, InMemoryRecentRequestStore>();
        services.AddSingleton<IAdminLiveFeed, GatewayAdminLiveFeed>();
        services.AddSingleton<IGatewayLogStore, InMemoryGatewayLogStore>();

        // The in-memory store is the floor: it serves the Errors tab on its own when no database is
        // configured, and the composition root swaps in the database-backed facade when one is.
        // Plain AddSingleton, not TryAdd: a later registration of the facade must win.
        services.AddSingleton<InMemoryGatewayErrorStore>();
        services.AddSingleton<IGatewayErrorStore>(sp => sp.GetRequiredService<InMemoryGatewayErrorStore>());
        services.AddSingleton<IGatewayErrorRecorder>(sp => sp.GetRequiredService<InMemoryGatewayErrorStore>());
        services.TryAddSingleton(TimeProvider.System);

        // Registering the sink as an ILoggerProvider in the container makes every ILogger warning
        // and error in the process visible in the admin Logs tab, with no call-site changes.
        // This only takes effect because the host opts into writeToProviders — see
        // GatewayHostBuilderExtensions.ConfigureGatewayHost.
        services.AddSingleton<ILoggerProvider>(sp =>
            new GatewayLogSinkProvider(
                sp.GetRequiredService<IGatewayLogStore>,
                sp.GetRequiredService<IGatewayErrorRecorder>,
                sp.GetRequiredService<IOptions<GatewayErrorTrackingOptions>>().Value));
        services.AddSingleton<IAdminSummaryReader, GatewayAdminSummaryReader>();
        services.AddSingleton<IControlPlaneCommands, ControlPlaneCommands>();
        services.AddSingleton<ChannelUsageRecorder>();
        services.AddSingleton<IUsageRecorder>(sp => sp.GetRequiredService<ChannelUsageRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelUsageRecorder>());
        services.AddHostedService<GatewayBackendHealthMetricsExporter>();
        services.AddHostedService<GatewayCircuitBreakerMetricsExporter>();

        return services;
    }
}
