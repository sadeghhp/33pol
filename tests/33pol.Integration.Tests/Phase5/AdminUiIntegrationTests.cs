using System.Net;
using Pol33.Integration.Tests.Admin;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUiIntegrationTests
{
    [Fact]
    public async Task GetAdminIndex_ReturnsSolidSpaShell()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/index.html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("33pol");
        body.Should().Contain("Gateway control plane");
        body.Should().Contain("id=\"root\"");
        body.Should().MatchRegex(@"<script[^>]+type=""module""[^>]+src=""/admin/assets/index-[^""]+\.js""");
        body.Should().MatchRegex(@"href=""/admin/assets/index-[^""]+\.css""");
        body.Should().NotContain("admin-app.js");
        body.Should().NotContain("admin.css?v=");
        body.Should().NotContain("x-data");
        body.Should().NotContain("alpine");
    }

    [Fact]
    public async Task GetAdminMainBundle_ContainsCoreConsoleRoutes()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var js = await AdminAssetTestHelpers.GetBundledAppJsAsync(client);

        js.Should().Contain("/admin/api/summary");
        js.Should().Contain("/admin/api/requests");
        js.Should().Contain("/admin/api/live");
        js.Should().Contain("/admin/api/config/status");

        var keysPage = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "KeysPage");
        keysPage.Should().Contain("/admin/api/keys");
        js.Should().Contain("33pol control plane");
        js.Should().Contain("gate-apiKey");
    }

    [Fact]
    public async Task GetAdminCss_ContainsShellStyles()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain(":root");
        css.Should().Contain("--accent");
        css.Should().Contain(".app-shell");
        css.Should().Contain(".hint");
    }

    [Fact]
    public async Task GetAdmin_RedirectsToIndex()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().Be("/admin/index.html");
    }

    [Fact]
    public async Task GetBackends_WithAdminKey_ReturnsRegistryBackends()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/backends");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("modelId");
    }
}
