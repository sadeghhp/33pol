using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Tests.Services;

public sealed class GatewayHealthServiceTests
{
    [Fact]
    public void GetHealth_AllUnhealthy_ReturnsDegraded503()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([new ModelConfig { Id = "m1", Url = "http://m1" }]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(false);
        health.GetHealth("m1").Returns((BackendHealth?)null);

        var service = new GatewayHealthService(registry, health, new GatewayProcessClock());

        var (body, statusCode) = service.GetHealth();

        body.Status.Should().Be("degraded");
        body.HealthyBackends.Should().Be(0);
        body.UnhealthyBackends.Should().Be(1);
        statusCode.Should().Be(503);
    }

    [Fact]
    public void GetHealth_AtLeastOneHealthy_ReturnsHealthy200()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "ok", Url = "http://ok" },
            new ModelConfig { Id = "bad", Url = "http://bad" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("ok").Returns(true);
        health.IsBackendHealthy("bad").Returns(false);

        var service = new GatewayHealthService(registry, health, new GatewayProcessClock());

        var (body, statusCode) = service.GetHealth();

        body.Status.Should().Be("healthy");
        body.HealthyBackends.Should().Be(1);
        body.UnhealthyBackends.Should().Be(1);
        statusCode.Should().Be(200);
    }
}
