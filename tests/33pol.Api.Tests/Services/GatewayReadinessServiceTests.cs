using Microsoft.AspNetCore.Http;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Tests.Services;

public sealed class GatewayReadinessServiceTests
{
    [Fact]
    public void GetReadiness_RegistryLoadedAndHealthy_Returns200()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 1 });
        config.IsReloadInProgress.Returns(false);

        var registry = Substitute.For<IModelRegistry>();
        // NSubstitute does not honour the interface default (IsLoaded => true).
        registry.IsLoaded.Returns(true);
        registry.GetAllModels().Returns([new ModelConfig { Id = "m1", Url = "http://x" }]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(true);

        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status200OK);
        body.Status.Should().Be("ready");
    }

    [Fact]
    public void GetReadiness_Draining_Returns503()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 1 });
        config.IsReloadInProgress.Returns(false);

        var registry = Substitute.For<IModelRegistry>();
        registry.IsLoaded.Returns(true);
        registry.GetAllModels().Returns([new ModelConfig { Id = "m1", Url = "http://x" }]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(true);

        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(true);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Status.Should().Be("not_ready");
        body.IsDraining.Should().BeTrue();
    }

    [Fact]
    public void GetReadiness_NoHealthyBackends_Returns503()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 1 });
        config.IsReloadInProgress.Returns(false);

        var registry = Substitute.For<IModelRegistry>();
        registry.IsLoaded.Returns(true);
        registry.GetAllModels().Returns([new ModelConfig { Id = "m1", Url = "http://x" }]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(false);

        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (_, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public void GetReadiness_EmptyButLoadedRegistry_Returns200()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 0 });
        config.IsReloadInProgress.Returns(false);

        var registry = Substitute.For<IModelRegistry>();
        registry.IsLoaded.Returns(true);
        registry.GetAllModels().Returns([]);

        var health = Substitute.For<IBackendHealthStore>();
        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status200OK);
        body.RegistryLoaded.Should().BeTrue();
        body.ModelCount.Should().Be(0);
    }

    [Fact]
    public void GetReadiness_NotLoaded_Returns503()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 0 });
        config.IsReloadInProgress.Returns(false);

        var registry = Substitute.For<IModelRegistry>();
        registry.IsLoaded.Returns(false);
        registry.GetAllModels().Returns([]);

        var health = Substitute.For<IBackendHealthStore>();
        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.RegistryLoaded.Should().BeFalse();
    }
}
