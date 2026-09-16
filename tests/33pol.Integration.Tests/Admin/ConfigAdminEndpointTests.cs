using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// These run against a gateway with a key store and call the control plane with a credential. The
/// shared no-database fixture they used to share served them anonymously, which is no longer true
/// of any configuration.
/// </summary>
public sealed class ConfigAdminEndpointTests
{
    [Fact]
    public async Task GetConfigStatus_ReturnsHotReloadShape()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/admin/api/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("hotReloadEnabled").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("modelCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        json.RootElement.GetProperty("models").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task PostConfigReload_WithValidConfig_ReturnsSuccess()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/admin/api/config/reload", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetString().Should().Be("success");
    }
}
