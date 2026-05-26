using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pol33.Integration.Tests.Admin;

public sealed class ConfigAdminEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ConfigAdminEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetConfigStatus_ReturnsHotReloadShape()
    {
        var response = await _client.GetAsync("/admin/api/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("hotReloadEnabled").GetBoolean().Should().BeTrue();
        json.RootElement.TryGetProperty("watchEnabled", out _).Should().BeTrue();
        json.RootElement.GetProperty("modelCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        json.RootElement.GetProperty("models").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task PostConfigReload_WithValidConfig_ReturnsSuccess()
    {
        var response = await _client.PostAsync("/admin/api/config/reload", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetString().Should().Be("success");
    }
}
