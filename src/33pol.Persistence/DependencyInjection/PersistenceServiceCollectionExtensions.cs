using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Persistence.Repositories;

namespace Pol33.Persistence.DependencyInjection;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<GatewayDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(GatewayDbContext).Assembly.FullName)));

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        return services;
    }
}
