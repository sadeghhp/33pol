using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Billing.DependencyInjection;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;

namespace Pol33.Billing.Tests.DependencyInjection;

public sealed class BillingPersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayBillingPersistence_WithConnectionString_ReplacesUsageHandler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = "InMemory:billing-di-test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);

        var usageHandler = services.Last(d => d.ServiceType == typeof(IUsagePersistenceHandler));
        usageHandler.ImplementationType.Should().Be(typeof(BillingUsageBatchPersistenceHandler));

        var budgetEnforcement = services.Last(d => d.ServiceType == typeof(IBudgetEnforcementService));
        budgetEnforcement.ImplementationType.Should().Be(typeof(BillingBudgetEnforcementService));
    }
}
