using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The secret store resolves the environment to decide whether a missing key pepper is fatal.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddOptions<GatewayOptions>().Configure(o =>
        {
            o.ModelsConfigPath = "config/models.json";
            o.ConfigReloadIntervalSeconds = 2;
        });
        services.AddGatewayRegistry();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IModelRegistry>().Should().BeOfType<ModelRegistryService>();
        provider.GetRequiredService<IModelRegistryWriter>().Should().BeOfType<ModelRegistryWriter>();
        provider.GetRequiredService<IConfigReload>().Should().BeOfType<ModelRegistryConfigReload>();
        provider.GetRequiredService<RegistryGate>().Should().NotBeNull();
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Development;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
