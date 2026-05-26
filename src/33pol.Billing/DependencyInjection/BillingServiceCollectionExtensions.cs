using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Billing.Aggregates;
using Pol33.Billing.Forecast;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Billing.Webhooks;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Billing.DependencyInjection;

public static class BillingServiceCollectionExtensions
{
    public static IServiceCollection AddGatewayBilling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName));

        services
            .AddOptions<BillingWebhookOptions>()
            .Bind(configuration.GetSection(BillingWebhookOptions.SectionName));

        services.AddHttpClient(nameof(BillingWebhookDispatcher));
        services.AddSingleton<IRateCardCostCalculator, RateCardCostCalculator>();
        services.AddSingleton<IDailyUsageRollupAggregator, DailyUsageRollupAggregator>();
        services.AddSingleton<BillingBudgetWarningTracker>();
        services.AddSingleton<BillingDailyUsageWebhookTracker>();
        services.AddSingleton<IBudgetEnforcementService, NoOpBudgetEnforcementService>();
        services.AddSingleton<IBillingWebhookDispatcher, BillingWebhookDispatcher>();
        services.AddSingleton<IBillingForecastService, NoOpBillingForecastService>();
        services.AddSingleton<IBillingUsageService, NoOpBillingUsageService>();
        services.AddSingleton<IUsagePersistenceHandler, NoOpUsagePersistenceHandler>();

        return services;
    }
}
