using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Persistence.DependencyInjection;
using Pol33.Security.DependencyInjection;
using Pol33.Security.Hosting;

namespace Pol33.Security.Tests.DependencyInjection;

public sealed class SecurityServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_RegistersNullValidator()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddGatewaySecurity(configuration);

        services.Any(d => d.ServiceType == typeof(IApiKeyValidator)).Should().BeTrue();
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GatewayAuthenticationInitializer))
            .Should().BeFalse();
    }

    [Fact]
    public void AddGatewaySecurity_WithConnectionString_RegistersAuthenticationServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}"] =
                    "Host=localhost;Database=test",
            })
            .Build();

        services.AddGatewaySecurity(configuration);

        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GatewayAuthenticationInitializer))
            .Should().BeTrue();
    }
}
