using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase3;

/// <summary>Phase 3 exit: v1 parity paths with auth and error headers.</summary>
[Trait("Category", "V1Parity")]
public sealed class Phase3IntegrationTests
{
    [Fact]
    public async Task Inference_WithRequestIdHeader_EchoesOnResponse()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req_integration_test");

        var response = await client.GetAsync("/health/live");

        response.Headers.GetValues("X-Request-Id").Single().Should().Be("req_integration_test");
    }

    [Fact]
    public async Task Inference_WithoutApiKeyWhenDbConfigured_Returns401WithErrorCode()
    {
        const string adminKey = "sk-33pol-phase3-exit-admin";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        using var body = new StringContent(
            """{"model":"gpt-local","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("X-33pol-Error-Code").Single().Should().Be("invalid_api_key");
    }

    [Fact]
    public async Task Inference_WithValidInferenceKey_Returns200()
    {
        const string adminKey = "sk-33pol-phase3-exit-admin-2";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            adminKey,
            upstreamHandler: new MockUpstreamHandler());
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

        var createResponse = await adminClient.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = System.Text.Json.JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var body = new StringContent(
            """{"model":"gpt-local","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await inferenceClient.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Request-Id").Should().NotBeEmpty();
    }
}
