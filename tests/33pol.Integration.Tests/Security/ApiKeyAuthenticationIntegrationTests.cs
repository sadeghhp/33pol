using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Infrastructure;

namespace Pol33.Integration.Tests.Security;

public sealed class ApiKeyAuthenticationIntegrationTests
{
    [Fact]
    public async Task GetV1Models_WithoutKey_WhenKeysConfigured_Returns401()
    {
        using var factory = CreateFactory(inferenceKeys: ["test-key"]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues("X-Request-Id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetV1Models_WithBearerKey_Returns200()
    {
        using var factory = CreateFactory(inferenceKeys: ["test-key"]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealth_Live_WithoutKey_Returns200()
    {
        using var factory = CreateFactory(inferenceKeys: ["test-key"]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostChatCompletions_WithMockUpstream_AndValidKey_Returns200()
    {
        using var factory = CreateFactory(inferenceKeys: ["test-key"]);
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-key");

        using var content = JsonContent.Create(new { model = "canonical-model", stream = false });
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static GatewayWebApplicationFactory CreateFactory(
        IReadOnlyList<string>? inferenceKeys = null,
        IReadOnlyList<string>? adminKeys = null) =>
        new(inferenceApiKeys: inferenceKeys, adminApiKeys: adminKeys);
}
