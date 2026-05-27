using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminModelWithApiKeyTests
{
    [Fact]
    public async Task PostModel_WithApiKey_PersistsSecretRef_NotKeyInRegistry()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var modelId = "quick-" + Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "https://openrouter.ai/api",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192
                },
                apiKey = "sk-or-test-upstream-key-1234567890"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("sk-or-test");

        var list = await client.GetFromJsonAsync<JsonElement>("/admin/api/models");
        list.ValueKind.Should().Be(JsonValueKind.Array);
        var entry = list.EnumerateArray().FirstOrDefault(e =>
            e.TryGetProperty("model", out var m) &&
            m.TryGetProperty("id", out var id) &&
            id.GetString() == modelId);

        entry.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        entry.GetProperty("model").GetProperty("upstreamAuth").GetProperty("secretRef").GetString()
            .Should().Be("file:model:" + modelId);
        entry.GetProperty("hasUpstreamCredential").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostModel_WithEnvVarNameInApiKeyField_Returns400()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = "bad-env-as-key",
                    url = "https://openrouter.ai/api",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192
                },
                apiKey = "OPENROUTER_API_KEY"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
