using System.Net;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUiSecurityTests
{
    [Fact]
    public async Task GetAdminJs_DoesNotPutProviderEnvVarInQueryString()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin.js?v=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("method: 'POST'");
        body.Should().Contain("JSON.stringify({ envVar");
        body.Should().NotContain("envVar=' +");
        body.Should().NotContain("envVar=\" +");
        body.Should().NotContain("?envVar=");
        body.Should().NotContain("?modelsUrl=");
    }

    [Fact]
    public async Task GetAdminJs_SetsNoStoreCacheControl()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin.js?v=2");

        response.Headers.CacheControl?.ToString().Should().Contain("no-store");
    }

    [Fact]
    public async Task PostModel_WithSecretUpstreamEnvVar_Returns400()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                id = "or-bad",
                url = "https://openrouter.ai/api",
                aliases = Array.Empty<string>(),
                maxContextLength = 8192,
                upstreamAuth = new { type = "bearer", envVar = "sk-or-v1-abcdef0123456789" }
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("not the API key");
    }
}
