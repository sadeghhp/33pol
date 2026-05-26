using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.Billing.DependencyInjection;

public static class BillingPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayBillingPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(PersistenceServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddScoped<BillingUsagePersistenceHandler>();
        services.AddScoped<BillingUsageService>();
        services.Replace(ServiceDescriptor.Scoped<IUsagePersistenceHandler, BillingUsagePersistenceHandler>());
        services.Replace(ServiceDescriptor.Scoped<IBillingUsageService, BillingUsageService>());
        return services;
    }
}
