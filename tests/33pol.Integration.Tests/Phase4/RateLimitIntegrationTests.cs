using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

[Trait("Category", "V1Parity")]
public sealed class RateLimitIntegrationTests
{
    [Fact]
    public async Task ChatCompletions_WhenRpmExceeded_Returns429WithRetryAfter()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.Create(
            handler,
            configureSettings: settings =>
            {
                settings["RateLimiting:Default:Rpm"] = "1";
                settings["RateLimiting:Default:Burst"] = "0";
            });

        var client = factory.CreateClient();

        var body = JsonSerializer.Serialize(new { model = "local-mock", messages = new[] { new { role = "user", content = "hi" } } });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var first = await client.PostAsync("/v1/chat/completions", content);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);

        using var content2 = new StringContent(body, Encoding.UTF8, "application/json");
        var second = await client.PostAsync("/v1/chat/completions", content2);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.Contains("Retry-After").Should().BeTrue();
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetProperty("code").GetString().Should().Be("rate_limit_exceeded");
    }
}
