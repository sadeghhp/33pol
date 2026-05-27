using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

public sealed class AdminModelsIntegrationTests
{
    [Fact]
    public async Task GetModels_WithAdminKey_ReturnsRegistryModels()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var models = await response.Content.ReadFromJsonAsync<JsonElement>();
        models.ValueKind.Should().Be(JsonValueKind.Array);
        models.GetArrayLength().Should().BeGreaterThan(0);
        models[0].GetProperty("model").GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        models[0].TryGetProperty("hasUpstreamCredential", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetModels_WithoutAdminKey_ReturnsUnauthorized()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        var response = await client.GetAsync("/admin/api/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
