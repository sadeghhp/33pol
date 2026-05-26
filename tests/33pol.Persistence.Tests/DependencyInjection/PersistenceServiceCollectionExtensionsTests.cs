using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.Persistence.Tests.DependencyInjection;

public sealed class PersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayPersistence_WithoutConnectionString_DoesNotRegisterRepositories()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddGatewayPersistence(configuration);

        services.Any(d => d.ServiceType == typeof(ITenantRepository)).Should().BeFalse();
    }

    [Fact]
    public void AddGatewayPersistence_WithConnectionString_RegistersRepositories()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}"] =
                    "Host=localhost;Database=test",
            })
            .Build();

        services.AddGatewayPersistence(configuration);

        services.Any(d => d.ServiceType == typeof(ITenantRepository)).Should().BeTrue();
        services.Any(d => d.ServiceType == typeof(IApiKeyRepository)).Should().BeTrue();
        services.Any(d => d.ServiceType == typeof(IModelGrantRepository)).Should().BeTrue();
    }
}
