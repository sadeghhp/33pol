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
        registry.GetAllModels().Returns([new ModelConfig { Id = "m1", Url = "http://x" }]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(false);

        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);

        var sut = new GatewayReadinessService(config, registry, health, drain);
        var (_, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
