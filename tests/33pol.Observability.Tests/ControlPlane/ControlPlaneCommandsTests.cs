using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.ControlPlane;

namespace Pol33.Observability.Tests.ControlPlane;

public sealed class ControlPlaneCommandsTests
{
    [Fact]
    public async Task ReloadConfigAsync_DelegatesToConfigReload()
    {
        var reload = Substitute.For<IConfigReload>();
        reload.ReloadAsync(Arg.Any<CancellationToken>())
            .Returns(ConfigReloadResult.Success("reloaded", 1, 1, []));

        var commands = CreateCommands(configReload: reload);
        var result = await commands.ReloadConfigAsync();

        result.Status.Should().Be("success");
        await reload.Received(1).ReloadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ListModels_ReturnsRegistryModels()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "m1", Url = "http://localhost:9000", Aliases = ["alias-1"] },
        ]);

        var commands = CreateCommands(registry: registry);
        var models = commands.ListModels();

        models.Should().ContainSingle();
        models[0].Id.Should().Be("m1");
        models[0].Aliases.Should().Contain("alias-1");
    }

    [Fact]
    public void ListBackends_MapsRegistryAndHealth()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "m1", Url = "http://localhost:9000" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(true);

        var commands = CreateCommands(registry: registry, healthStore: health);
        var backends = commands.ListBackends();

        backends.Should().ContainSingle();
        backends[0].ModelId.Should().Be("m1");
        backends[0].IsHealthy.Should().BeTrue();
    }

    private static ControlPlaneCommands CreateCommands(
        IConfigReload? configReload = null,
        IModelRegistry? registry = null,
        IBackendHealthStore? healthStore = null)
    {
        configReload ??= Substitute.For<IConfigReload>();
        registry ??= Substitute.For<IModelRegistry>();
        healthStore ??= Substitute.For<IBackendHealthStore>();
        var summary = Substitute.For<IAdminSummaryReader>();
        var recent = Substitute.For<IRecentRequestStore>();
        var writer = Substitute.For<IModelRegistryWriter>();

        return new ControlPlaneCommands(
            configReload,
            registry,
            healthStore,
            summary,
            recent,
            writer);
    }
}
