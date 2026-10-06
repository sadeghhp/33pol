using System.Net;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Admin;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUiSecurityTests
{
    [Fact]
    public async Task GetAdminBundle_DoesNotPutSecretsInQueryStrings()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var js = await AdminAssetTestHelpers.GetBundledAppJsAsync(client);

        js.Should().NotContain("?envVar=");
        js.Should().NotContain("?apiKey=");
        js.Should().NotContain("fetchProviderModels");
    }

    [Fact]
    public async Task GetAdminMainBundle_IsImmutablyCached()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var path = AdminAssetTestHelpers.ExtractMainBundlePath(html);
        var response = await client.GetAsync(path);

        response.Headers.CacheControl!.ToString().Should().Contain("immutable");
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
                model = new
                {
                    id = "or-bad",
                    url = "https://openrouter.ai/api",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192,
                    upstreamAuth = new { type = "bearer", envVar = "sk-or-v1-abcdef0123456789" }
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("not the API key");
    }
}
