using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Host;

public sealed class GatewayHostStartupTests
{
    [Fact]
    public async Task Host_WithSerilogPipeline_ServesHealthLive()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Host_ModelRouterRegistered_AfterMinimalApiEndpoints()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var models = await client.GetAsync("/v1/models");
        models.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var inference = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"local-mock"}""", System.Text.Encoding.UTF8, "application/json"));

        inference.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        handler.SendCount.Should().Be(1);
    }
}
