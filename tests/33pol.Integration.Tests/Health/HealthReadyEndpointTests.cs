using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Health;

public sealed class HealthReadyEndpointTests
{
    [Fact]
    public async Task GetHealthReady_WhenHealthy_ReturnsOk()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"ready\"");
    }

    [Fact]
    public async Task GetHealthReady_WhenDraining_Returns503()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IGatewayDrainState>().BeginDrain();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"isDraining\":true");
    }

    [Fact]
    public async Task GetHealthReady_AllBackendsUnhealthy_Returns503()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            healthStore: new AlwaysUnhealthyBackendHealthStore());
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetHealthLive_RemainsPublicAndOk()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
