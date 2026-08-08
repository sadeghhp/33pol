using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Hosting;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Hosting;

public sealed class ModelRegistryLoaderHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenLoaderSucceeds_Completes()
    {
        var services = new ServiceCollection();
        var repository = Substitute.For<IModelRouteRepository>();
        repository.ListWithVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ModelRouteSnapshot(
                [new ModelConfig { Id = "m1", Url = "http://localhost:1" }],
                Version: 3));
        services.AddSingleton(repository);
        await using var provider = services.BuildServiceProvider();

        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var loader = new ModelRegistryLoader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            Options.Create(new GatewayOptions { ModelsConfigPath = "missing.json" }),
            NullLogger<ModelRegistryLoader>.Instance);

        var hosted = new ModelRegistryLoaderHostedService(
            loader,
            NullLogger<ModelRegistryLoaderHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        registry.GetAllModels().Should().ContainSingle(m => m.Id == "m1");
    }

    [Fact]
    public async Task StartAsync_WhenLoaderThrows_DoesNotCrashHost()
    {
        var services = new ServiceCollection();
        var repository = Substitute.For<IModelRouteRepository>();
        repository.ListWithVersionAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ModelRouteSnapshot>>(_ => throw new InvalidOperationException("db down"));
        services.AddSingleton(repository);
        await using var provider = services.BuildServiceProvider();

        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var loader = new ModelRegistryLoader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            Options.Create(new GatewayOptions { ModelsConfigPath = "missing.json" }),
            NullLogger<ModelRegistryLoader>.Instance);

        var hosted = new ModelRegistryLoaderHostedService(
            loader,
            NullLogger<ModelRegistryLoaderHostedService>.Instance);

        var act = async () => await hosted.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
