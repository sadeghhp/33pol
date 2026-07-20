using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Billing.Forecast;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Billing.Webhooks;
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

        services.Replace(ServiceDescriptor.Scoped<IRateCardAdminService, RateCardAdminService>());

        services.AddScoped<BillingUsagePersistenceHandler>();
        services.AddScoped<BillingUsageService>();
        services.AddScoped<IBillingForecastService, BillingForecastService>();
        services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcementService, BillingBudgetEnforcementService>());

        services.AddSingleton<BillingUsageBatchPersistenceHandler>();
        services.AddHostedService(sp => sp.GetRequiredService<BillingUsageBatchPersistenceHandler>());
        services.Replace(ServiceDescriptor.Singleton<IUsagePersistenceHandler, BillingUsageBatchPersistenceHandler>());
        services.Replace(ServiceDescriptor.Scoped<IBillingUsageService, BillingUsageService>());
        services.AddScoped<DailyUsageWebhookPublisher>();
        services.AddHostedService<DailyUsageWebhookHostedService>();
        return services;
    }
}
