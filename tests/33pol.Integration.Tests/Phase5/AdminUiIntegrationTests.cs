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
        body.Should().Contain("33pol control plane");
        body.Should().MatchRegex("href=\"admin\\.css\\?v=\\d+\"");
        body.Should().MatchRegex("src=\"admin-icons\\.js\\?v=\\d+\"");
        body.Should().MatchRegex("src=\"admin-errors\\.js\\?v=\\d+\"");
        body.Should().MatchRegex("src=\"admin-store\\.js\\?v=\\d+\"");
        body.Should().MatchRegex("src=\"admin-app\\.js\\?v=\\d+\"");
        body.Should().Contain("keyAccessDrawerOpen");
        body.Should().Contain("keysEditDrawerOpen");
        body.Should().Contain("usageFilterApiKeyId");
        body.Should().Contain("Assignee");
        body.Should().Contain("Tenant model access");
        body.Should().Contain("CORS allowed origins");
        body.Should().Contain("x-show=\"isSettingsCors\"");
        body.Should().Contain("rerunModelTest");
        body.Should().Contain("model-test-title");
        // The test dialog describes the probe for this model's type instead of a hardcoded chat prompt.
        body.Should().Contain("x-text=\"modelTest.hint\"");
        body.Should().NotContain("Hello world");
        body.Should().Contain("x-model=\"mdl.editModel.modelType\"");
        body.Should().Contain("x-text=\"m.typeLabel\"");
        body.Should().NotContain("Discover from provider");
        body.Should().Contain("id=\"model-upstream-api-key\"");
        body.Should().Contain("x-model=\"mdl.editModel.apiKey\"");
        body.Should().NotMatchRegex(@"<label[^>]*x-text=[^>]*>[\s\S]*?x-model=""mdl\.editModel\.apiKey""");
        body.Should().Contain("class=\"hint\"");
        body.Should().Contain("x-model=\"mdl.editModel.inputPricePerMillion\"");
        body.Should().Contain("x-model=\"mdl.editModel.outputPricePerMillion\"");
        body.Should().Contain("Input price (per 1M tokens)");
        body.Should().Contain("x-text=\"m.price\"");
        body.Should().Contain("Errors by model");
        body.Should().Contain("x-model=\"mdl.requestsErrorsOnly\"");
        body.Should().Contain("Errors only");
        body.Should().Contain("Request ID");
        body.Should().Contain("errorModelBars");
        body.Should().Contain("aria-expanded");
        body.Should().Contain("toggleShowModelApiKey");
        body.Should().Contain("x-cloak");
        body.Should().Contain("role=\"tabpanel\"");
        body.Should().Contain("app-shell");
        body.Should().Contain("id=\"gate-apiKey\"");
        body.Should().Contain("x-model=\"mdl.gateApiKey\"");
        body.Should().NotMatchRegex("id=\"gate-apiKey\"[^>]*x-model=\"mdl\\.apiKey\"");
        // Both key inputs write to a draft. Bound to the live key, every keystroke replaced the
        // credential the 2s poll was using, so typing a replacement 401'd the working session.
        body.Should().Contain("x-model=\"mdl.headerApiKey\"");
        body.Should().NotContain("x-model=\"mdl.apiKey\"");
        body.Should().NotContain("function adminApp()");
        body.Should().NotContain("confirm(");
        // The CSP-friendly Alpine build resolves x-data through Alpine.data, not by calling adminApp().
        body.Should().Contain("x-data=\"adminApp\"");
        body.Should().Contain("vendor/alpine-csp-3.14.9.min.js");
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
        body.Should().Contain("_loadingDepth");
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
        body.Should().Contain(".hint");
        body.Should().Contain(".hbars");
        body.Should().Contain(".request-detail-row");
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
        body.Should().Contain("async fetchModels()");
        body.Should().Contain("normalizeApiKeyList");
        body.Should().Contain("normalizeApiKeyRole");
        body.Should().Contain("async fetchKeys()");
        body.Should().Contain("includeUsageSummary=true");
        body.Should().Contain("saveKeyEdit");
        body.Should().Contain("viewKeyUsage");
        body.Should().Contain("async fetchBackends()");
        body.Should().Contain("async loadSettings()");
        body.Should().Contain("async loadCors()");
        body.Should().Contain("async saveCors()");
        body.Should().Contain("/admin/api/cors");
        body.Should().MatchRegex("async revokeKeyConfirmed\\(\\)[\\s\\S]*?await this\\.fetchKeys\\(\\)");
        body.Should().MatchRegex("async removeModel\\(id\\)[\\s\\S]*?await this\\.fetchModels\\(\\)");
        body.Should().Contain("testModel");
        body.Should().Contain("resolveModelType");
        body.Should().Contain("modelTestHint");
        body.Should().Contain("/models/");
        body.Should().Contain("apiKey");
        body.Should().Contain("gateApiKey");
        body.Should().Contain("errorModelBars");
        body.Should().Contain("requestsErrorsOnly");
        body.Should().Contain("shortRequestId");
        body.Should().MatchRegex("loadSummary\\([\\s\\S]*?/admin/api/requests");
        body.Should().Contain("downloadBlob");
        body.Should().Contain("formatModelPrice");
        body.Should().Contain("modelPricingError");
        body.Should().Contain("clearPricing");
        body.Should().NotContain("confirm(");
        body.Should().Contain("Alpine.data('adminApp'");
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
