using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

public sealed class AdminSummaryIntegrationTests
{
    [Fact]
    public async Task GetSummary_WithAdminKey_ReturnsSnapshot()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("uptime").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("totalInferenceRequests").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }
}
