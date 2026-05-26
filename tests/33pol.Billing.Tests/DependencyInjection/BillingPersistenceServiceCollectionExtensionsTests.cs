using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.Billing.DependencyInjection;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.Billing.Tests.DependencyInjection;

public sealed class BillingPersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayBillingPersistence_WithoutConnectionString_DoesNotRegisterBatchHandler()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddGatewayBilling(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>()
            .Should()
            .NotContain(s => s is BillingUsageBatchPersistenceHandler);
    }

    [Fact]
    public void AddGatewayBillingPersistence_WithInMemoryDb_RegistersPersistenceServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = "InMemory:billing-di-test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPersistence(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IBillingUsageService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IBudgetEnforcementService>().Should().NotBeOfType<NoOpBudgetEnforcementService>();
        provider.GetServices<IHostedService>()
            .Should()
            .Contain(s => s is BillingUsageBatchPersistenceHandler);
    }
}
