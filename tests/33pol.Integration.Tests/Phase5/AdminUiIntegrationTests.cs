using System.Net;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUiIntegrationTests
{
    [Fact]
    public async Task GetAdminIndex_ReturnsHtml()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/index.html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("33pol Gateway Admin");
        body.Should().Contain("href=\"admin.css?v=4\"");
        body.Should().Contain("src=\"admin-errors.js?v=4\"");
        body.Should().Contain("src=\"admin-store.js?v=4\"");
        body.Should().Contain("src=\"admin-app.js?v=4\"");
        body.Should().NotContain("Discover from provider");
        body.Should().Contain("type=\"password\"");
        body.Should().Contain("x-model=\"editModel.apiKey\"");
        body.Should().Contain("x-cloak");
        body.Should().Contain("role=\"tabpanel\"");
        body.Should().Contain("app-shell");
        body.Should().NotContain("function adminApp()");
        body.Should().NotContain("confirm(");
    }

    [Fact]
    public async Task GetAdminStore_ReturnsAlpineStore()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin-store.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Alpine.store('admin'");
        body.Should().Contain("withLoading");
        body.Should().Contain("downloadBlob");
    }

    [Fact]
    public async Task GetAdminErrors_ReturnsClassifier()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin-errors.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("AdminErrors");
        body.Should().Contain("classifyError");
    }

    [Fact]
    public async Task GetAdminCss_ReturnsStylesheet()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(":root");
        body.Should().Contain("--accent");
        body.Should().Contain("app-shell");
    }

    [Fact]
    public async Task GetAdminApp_ReturnsAdminApp()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin-app.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("function adminApp()");
        body.Should().Contain("/admin/api/summary");
        body.Should().Contain("modelWriteBody");
        body.Should().Contain("apiKey");
        body.Should().Contain("downloadBlob");
        body.Should().NotContain("confirm(");
        body.Should().NotContain("openDiscover");
        body.Should().NotContain("fetchProviderModels");
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
