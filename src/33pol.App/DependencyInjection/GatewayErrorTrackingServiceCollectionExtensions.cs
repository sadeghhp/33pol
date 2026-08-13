using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Observability.Diagnostics;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.App.DependencyInjection;

public static class GatewayErrorTrackingServiceCollectionExtensions
{
    /// <summary>
    /// Wires the durable error store behind the admin Errors tab.
    /// </summary>
    /// <remarks>
    /// Degrades rather than disappears. With no database configured the in-memory store registered
    /// by <c>AddGatewayObservability</c> stays in place and the tab still works — it simply reports
    /// <c>persisted: false</c> and loses its history on restart. Must run after
    /// <c>AddGatewayPersistence</c> and <c>AddGatewayStatsPersistence</c>, whose registrations it
    /// builds on.
    /// </remarks>
    public static IServiceCollection AddGatewayErrorTracking(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<GatewayErrorTrackingOptions>()
            .Bind(configuration.GetSection(GatewayErrorTrackingOptions.SectionName));

        // Needed with or without a database: the coordinator guards the counter reset, and the admin
        // service performs it.
        services.AddSingleton<GatewayStatsFlushCoordinator>();
        services.AddSingleton<IGatewayErrorAdminService, GatewayErrorAdminService>();

        var connectionString = configuration.GetConnectionString(
            PersistenceServiceCollectionExtensions.ConnectionStringName);

        var persistenceEnabled = configuration
            .GetSection(GatewayErrorTrackingOptions.SectionName)
            .GetValue("PersistToDatabase", defaultValue: true);

        if (string.IsNullOrWhiteSpace(connectionString) || !persistenceEnabled)
        {
            return services;
        }

        services.AddSingleton<GatewayErrorBatchPersistenceHandler>();
        services.AddSingleton<IGatewayErrorArchiveWriter>(sp =>
            sp.GetRequiredService<GatewayErrorBatchPersistenceHandler>());
        services.AddHostedService(sp => sp.GetRequiredService<GatewayErrorBatchPersistenceHandler>());

        // Overrides the in-memory registration from AddGatewayObservability — last one wins.
        services.AddSingleton<IGatewayErrorStore, DatabaseGatewayErrorStore>();

        services.AddHostedService<GatewayErrorRetentionService>();

        return services;
    }
}
