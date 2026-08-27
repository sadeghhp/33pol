using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Persistence.Bootstrap;
using Pol33.Persistence.Hosting;
using Pol33.Persistence.Infrastructure;
using Pol33.Persistence.Repositories;

namespace Pol33.Persistence.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "GatewayDb";

    public static IServiceCollection AddGatewayPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<ISqliteBackupService, Maintenance.NullSqliteBackupService>();
            return services;
        }

        if (connectionString.StartsWith("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var databaseName = connectionString.Contains(':')
                ? connectionString[(connectionString.IndexOf(':') + 1)..]
                : Guid.NewGuid().ToString("N");
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            services.AddSingleton<ISqliteBackupService, Maintenance.NullSqliteBackupService>();
        }
        else
        {
            // Pooled: a context is resolved for every authenticated inference request (the API-key
            // validator, grant service and budget check all sit behind scoped repositories) even
            // when every one of them is answered from cache. Renting a reset instance from the pool
            // instead of building change tracker, state manager and service provider anew each time
            // takes that steady per-request allocation off the hot path. GatewayDbContext holds no
            // per-request state, which is the precondition for pooling.
            services.AddDbContextPool<GatewayDbContext>(options =>
                SqliteGatewayDbContext.Configure(options, connectionString));
            services.AddScoped<ISqliteBackupService, Maintenance.SqliteBackupService>();
        }

        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<GatewayBootstrapOptions>, GatewayBootstrapOptionsValidator>();
        services
            .AddOptions<GatewayBootstrapOptions>()
            .Bind(configuration.GetSection(GatewayBootstrapOptions.SectionName))
            .ValidateOnStart();

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IModelGrantRepository, ModelGrantRepository>();
        services.AddScoped<IApiKeyModelGrantRepository, ApiKeyModelGrantRepository>();
        services.AddScoped<IApiKeyLifecycleEventRepository, ApiKeyLifecycleEventRepository>();
        services.AddScoped<IDailyUsageRollupRepository, DailyUsageRollupRepository>();
        services.AddScoped<IBillingEventRepository, BillingEventRepository>();
        services.AddMemoryCache();
        services.AddScoped<RateCardRepository>();
        services.AddScoped<IRateCardRepository, CachingRateCardRepository>();
        services.AddScoped<BudgetRepository>();
        services.AddScoped<IBudgetRepository, CachingBudgetRepository>();
        services.AddScoped<IGatewayStatsSnapshotStore, GatewayStatsSnapshotStore>();
        services.AddScoped<IMaintenanceStateStore, Maintenance.MaintenanceStateStore>();
        services.AddScoped<ISqliteDatabaseInfo, Maintenance.SqliteDatabaseInfo>();
        services.AddScoped<IGatewayErrorArchive, GatewayErrorRepository>();
        services.AddScoped<IQuotaUsageSnapshotStore, QuotaUsageSnapshotStore>();
        services.AddScoped<IGatewayConfigStore, GatewayConfigStore>();
        services.AddScoped<ICorsSettingsRepository, CorsSettingsRepository>();
        services.AddScoped<IRateLimitSettingsRepository, RateLimitSettingsRepository>();
        services.AddScoped<IModelRouteRepository, ModelRouteRepository>();
        services.AddScoped<GatewayDbBootstrap>();
        services.AddHostedService<GatewayDbInitializer>();

        return services;
    }
}
