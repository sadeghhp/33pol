using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

public sealed class ResilienceIntegrationTests
{
    [Fact]
    public async Task PostInference_WhenDraining_Returns503GatewayDraining()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        using var scope = factory.Services.CreateScope();
        var drain = scope.ServiceProvider.GetRequiredService<IGatewayDrainState>();
        drain.BeginDrain();

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"gpt-local","stream":false}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryGetValues("X-33pol-Error-Code", out var codes).Should().BeTrue();
        codes!.Single().Should().Be("gateway_draining");
    }

    [Fact]
    public async Task PostInference_ContentLengthOverLimit_Returns400RequestTooLarge()
    {
        await using var factory = GatewayWebApplicationFactory.Create(configureConfiguration: config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Resilience:MaxRequestBodyBytes"] = "10",
            });
        });

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"local-mock","stream":false,"prompt":"too-large"}""",
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentLength = 100;
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("X-33pol-Error-Code", out var codes).Should().BeTrue();
        codes!.Single().Should().Be("request_too_large");
    }
}
