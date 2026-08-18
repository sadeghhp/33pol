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

    /// <summary>
    /// The anonymous shape carries up/down per backend but never the upstream URL or the prober's
    /// error text; the full shape is for operators.
    /// </summary>
    [Fact]
    public void GetHealthSummary_OmitsUrlAndErrorButKeepsCountsAndStatus()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "ok", Url = "http://10.0.0.5:8000" },
            new ModelConfig { Id = "bad", Url = "http://10.0.0.6:8000" },
        ]);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("ok").Returns(true);
        health.IsBackendHealthy("bad").Returns(false);
        var checkedAt = DateTimeOffset.UtcNow;
        health.GetHealth("bad").Returns(new BackendHealth(
            "bad",
            "http://10.0.0.6:8000",
            false,
            null,
            "connection refused to 10.0.0.6",
            checkedAt));

        var service = new GatewayHealthService(registry, health, new GatewayProcessClock());

        var (summary, summaryStatus) = service.GetHealthSummary();
        var (full, fullStatus) = service.GetHealth();

        summaryStatus.Should().Be(fullStatus).And.Be(200);
        summary.Status.Should().Be(full.Status);
        summary.TotalBackends.Should().Be(2);
        summary.HealthyBackends.Should().Be(1);
        summary.UnhealthyBackends.Should().Be(1);
        summary.Backends.Should().HaveCount(2);
        var bad = summary.Backends.Single(b => b.ModelId == "bad");
        bad.IsHealthy.Should().BeFalse();
        bad.LastChecked.Should().Be(checkedAt);
        typeof(Pol33.Api.Contracts.GatewayBackendHealthSummaryEntry).GetProperty("Url").Should().BeNull();
        typeof(Pol33.Api.Contracts.GatewayBackendHealthSummaryEntry).GetProperty("Error").Should().BeNull();

        full.Backends.Single(b => b.ModelId == "bad").Error.Should().Contain("10.0.0.6");
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
