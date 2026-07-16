using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Configuration;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.App.DependencyInjection;

public static class GatewayStatsPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Wires the background service that persists and restores the dashboard counters and monthly
    /// quota usage. No-op unless a database connection string is configured (mirrors
    /// <c>AddGatewayBillingPersistence</c>). Must be registered after <c>AddGatewayPersistence</c>
    /// so database migrations run before this service's startup restore.
    /// </summary>
    public static IServiceCollection AddGatewayStatsPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(
            PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services
            .AddOptions<GatewayStatsPersistenceOptions>()
            .Bind(configuration.GetSection(GatewayStatsPersistenceOptions.SectionName));

        services.AddHostedService<GatewayStatsSnapshotService>();

        return services;
    }
}
