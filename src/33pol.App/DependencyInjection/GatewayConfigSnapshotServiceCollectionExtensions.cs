using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.App.DependencyInjection;

public static class GatewayConfigSnapshotServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database-backed configuration snapshot. <see cref="IGatewayConfigProvider"/> is
    /// always registered (serving <see cref="GatewayConfigSnapshot.Defaults"/> when no database is
    /// configured); the syncer and <see cref="IGatewayConfigRefresher"/> are added only when a
    /// connection string is present, and must run after <c>AddGatewayPersistence</c> so migrations
    /// apply before the first load.
    /// </summary>
    public static IServiceCollection AddGatewayConfigSnapshot(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<GatewayConfigState>();
        services.AddSingleton<IGatewayConfigProvider>(sp => sp.GetRequiredService<GatewayConfigState>());

        var connectionString = configuration.GetConnectionString(
            PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services
            .AddOptions<GatewayConfigSnapshotOptions>()
            .Bind(configuration.GetSection(GatewayConfigSnapshotOptions.SectionName));

        services.AddSingleton<GatewayConfigSnapshotService>();
        services.AddSingleton<IGatewayConfigRefresher>(sp => sp.GetRequiredService<GatewayConfigSnapshotService>());
        services.AddHostedService(sp => sp.GetRequiredService<GatewayConfigSnapshotService>());

        return services;
    }
}
