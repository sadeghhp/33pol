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
        health.GetHealth("m1").Returns(Probed(isHealthy: true));

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
        health.GetHealth("m1").Returns(Probed(isHealthy: true));

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
        health.GetHealth("m1").Returns(Probed(isHealthy: false));

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

    /// <summary>
    /// The GW-02a regression. A model the sweep has not reached a verdict on used to count as
    /// healthy — <c>IsBackendHealthy</c> answers <c>!HealthCheckStrictMode</c>, true by default, for
    /// an unknown model — so a pod passed its readiness gate and took traffic on every rolling
    /// restart before a single upstream had been proven reachable. Unprobed is not ready.
    /// </summary>
    [Fact]
    public void GetReadiness_ConfiguredButNeverProbed_Returns503()
    {
        var health = Substitute.For<IBackendHealthStore>();
        health.GetHealth("m1").Returns((BackendHealth?)null);
        // The optimistic accessor would say yes. Readiness must not ask it.
        health.IsBackendHealthy("m1").Returns(true);

        var sut = new GatewayReadinessService(Config(), Registry(Model("m1")), health, NotDraining());
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.ConfiguredBackends.Should().Be(1);
        body.ProbedBackends.Should().Be(0, "no sweep has reached a verdict yet");
        body.HealthyBackends.Should().Be(0);
    }

    /// <summary>One proven backend is enough to serve, so it is enough to be ready.</summary>
    [Fact]
    public void GetReadiness_OneHealthyOfTwo_Returns200()
    {
        var health = Substitute.For<IBackendHealthStore>();
        health.GetHealth("m1").Returns(Probed(isHealthy: false));
        health.GetHealth("m2").Returns(Probed(isHealthy: true));

        var sut = new GatewayReadinessService(
            Config(), Registry(Model("m1"), Model("m2")), health, NotDraining());
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status200OK);
        body.ConfiguredBackends.Should().Be(2);
        body.ProbedBackends.Should().Be(2);
        body.HealthyBackends.Should().Be(1);
    }

    /// <summary>
    /// A route an operator stopped is not a backend the gateway failed to reach, so it neither
    /// counts toward readiness nor withholds it. A registry of nothing but stopped routes is the
    /// empty case: ready, and reported by gateway_models_configured rather than by a 503.
    /// </summary>
    [Fact]
    public void GetReadiness_OnlyStoppedRoutes_IsTreatedAsEmptyAndReturns200()
    {
        var health = Substitute.For<IBackendHealthStore>();
        health.GetHealth(Arg.Any<string>()).Returns((BackendHealth?)null);

        var sut = new GatewayReadinessService(
            Config(), Registry(Model("m1", ModelRouteStates.Stopped)), health, NotDraining());
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status200OK);
        body.ModelCount.Should().Be(1);
        body.ConfiguredBackends.Should().Be(0, "a stopped route is not one the gateway will serve");
    }

    /// <summary>A stopped route alongside a healthy one does not drag readiness down either.</summary>
    [Fact]
    public void GetReadiness_StoppedRouteAlongsideAHealthyOne_Returns200()
    {
        var health = Substitute.For<IBackendHealthStore>();
        health.GetHealth("m1").Returns(Probed(isHealthy: true));
        health.GetHealth("m2").Returns((BackendHealth?)null);

        var sut = new GatewayReadinessService(
            Config(),
            Registry(Model("m1"), Model("m2", ModelRouteStates.Stopped)),
            health,
            NotDraining());
        var (body, status) = sut.GetReadiness();

        status.Should().Be(StatusCodes.Status200OK);
        body.ConfiguredBackends.Should().Be(1);
        body.HealthyBackends.Should().Be(1);
    }

    private static BackendHealth Probed(bool isHealthy) =>
        new("m", "http://x", isHealthy, isHealthy ? 200 : 502, null, DateTimeOffset.UtcNow);

    private static ModelConfig Model(string id, string? state = null) =>
        new() { Id = id, Url = "http://x", State = state ?? ModelRouteStates.Serving };

    private static IConfigReload Config()
    {
        var config = Substitute.For<IConfigReload>();
        config.GetStatus().Returns(new ConfigStatusResponse { ModelCount = 1 });
        config.IsReloadInProgress.Returns(false);
        return config;
    }

    private static IModelRegistry Registry(params ModelConfig[] models)
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.IsLoaded.Returns(true);
        registry.GetAllModels().Returns(models);
        return registry;
    }

    private static IGatewayDrainState NotDraining()
    {
        var drain = Substitute.For<IGatewayDrainState>();
        drain.IsDraining.Returns(false);
        return drain;
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
