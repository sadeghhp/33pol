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
        // The initial snapshot is sourced from appsettings so there is no config gap before the first
        // database load, and so a DB-less deployment still serves its configured CORS origins. When a
        // database is present the syncer overwrites this with the DB copy (seeded from the same
        // appsettings on first boot).
        var initial = BuildInitialSnapshot(configuration);
        services.AddSingleton(new GatewayConfigState(initial));
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

    private static GatewayConfigSnapshot BuildInitialSnapshot(IConfiguration configuration)
    {
        var origins = configuration
            .GetSection($"{GatewayOptions.SectionName}:{GatewayCorsOptions.SectionName}:{nameof(GatewayCorsOptions.AllowedOrigins)}")
            .Get<string[]>();

        return new GatewayConfigSnapshot
        {
            Cors = new CorsConfigSection
            {
                AllowedOrigins = GatewayCorsOptions.NormalizeOrigins(origins),
            },
        };
    }
}
