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

public sealed class ModelRegistryRouteReconcileServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenRouteVersionChanges_ReloadsRegistry()
    {
        var services = new ServiceCollection();
        var repository = Substitute.For<IModelRouteRepository>();
        repository.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(7);
        repository.ListWithVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ModelRouteSnapshot(
                [new ModelConfig { Id = "m1", Url = "http://localhost:1" }],
                Version: 7));
        services.AddSingleton(repository);
        await using var provider = services.BuildServiceProvider();

        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var loader = new ModelRegistryLoader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            Options.Create(new GatewayOptions { ModelsConfigPath = "missing.json", ConfigReloadIntervalSeconds = 1 }),
            NullLogger<ModelRegistryLoader>.Instance);
        var gate = new RegistryGate();
        var service = new ModelRegistryRouteReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            loader,
            registry,
            gate,
            Options.Create(new GatewayOptions { ConfigReloadIntervalSeconds = 1 }),
            NullLogger<ModelRegistryRouteReconcileService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await service.StartAsync(cts.Token);
            await WaitUntilAsync(() => registry.AppliedRouteVersion == 7, TimeSpan.FromSeconds(2.5));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        registry.AppliedRouteVersion.Should().Be(7);
        registry.GetAllModels().Should().ContainSingle(m => m.Id == "m1");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRepository_KeepsCurrentRegistry()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        registry.Apply([new ModelConfig { Id = "seed", Url = "http://localhost:1" }], routeVersion: 1);

        var loader = new ModelRegistryLoader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registry,
            Options.Create(new GatewayOptions { ModelsConfigPath = "missing.json", ConfigReloadIntervalSeconds = 1 }),
            NullLogger<ModelRegistryLoader>.Instance);

        var service = new ModelRegistryRouteReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            loader,
            registry,
            new RegistryGate(),
            Options.Create(new GatewayOptions { ConfigReloadIntervalSeconds = 1 }),
            NullLogger<ModelRegistryRouteReconcileService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(1100, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // expected when the short CTS cancels the background loop
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        registry.GetAllModels().Should().ContainSingle(m => m.Id == "seed");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
}
