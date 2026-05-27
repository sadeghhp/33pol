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
        body.Should().Contain("href=\"admin.css\"");
        body.Should().Contain("src=\"admin.js\"");
        body.Should().NotContain("function adminApp()");
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
    }

    [Fact]
    public async Task GetAdminJs_ReturnsAdminApp()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("function adminApp()");
        body.Should().Contain("/admin/api/summary");
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
