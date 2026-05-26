using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Registry.DependencyInjection;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.DependencyInjection;

public sealed class RegistryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayRegistry_RegistersRegistryAbstractions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<GatewayOptions>().Configure(o =>
        {
            o.ModelsConfigPath = "config/models.json";
            o.ConfigReloadIntervalSeconds = 2;
        });
        services.AddGatewayRegistry();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IModelRegistry>().Should().BeOfType<ModelRegistryService>();
        provider.GetRequiredService<IModelRegistryWriter>().Should().BeOfType<ModelRegistryWriter>();
        provider.GetRequiredService<IConfigReload>().Should().BeOfType<ConfigReloadService>();
        provider.GetRequiredService<RegistryGate>().Should().NotBeNull();
    }
}
