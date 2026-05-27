using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Persistence.Bootstrap;
using Pol33.Persistence.Hosting;
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
            return services;
        }

        if (connectionString.StartsWith("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var databaseName = connectionString.Contains(':')
                ? connectionString[(connectionString.IndexOf(':') + 1)..]
                : Guid.NewGuid().ToString("N");
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
        }
        else
        {
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsAssembly(typeof(GatewayDbContext).Assembly.GetName().Name)));
        }

        services.Configure<GatewayBootstrapOptions>(
            configuration.GetSection(GatewayBootstrapOptions.SectionName));

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IModelGrantRepository, ModelGrantRepository>();
        services.AddScoped<IApiKeyModelGrantRepository, ApiKeyModelGrantRepository>();
        services.AddScoped<IDailyUsageRollupRepository, DailyUsageRollupRepository>();
        services.AddScoped<IBillingEventRepository, BillingEventRepository>();
        services.AddScoped<IRateCardRepository, RateCardRepository>();
        services.AddScoped<IBudgetRepository, BudgetRepository>();
        services.AddScoped<GatewayDbBootstrap>();
        services.AddHostedService<GatewayDbInitializer>();

        return services;
    }
}
