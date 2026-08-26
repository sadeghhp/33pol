using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Scoped rules written in <c>appsettings.json</c>, end to end on a database-backed gateway.
/// </summary>
/// <remarks>
/// Only the admin API path was covered before, and it was the only path that worked: the bootstrap
/// seeded the default tier and the plan tiers and nothing else, so every <c>Models</c>,
/// <c>ApiKeys</c>, <c>TenantModels</c>, <c>ApiKeyModels</c>, <c>Tenants</c>, <c>Global</c> and
/// <c>AuthFailure</c> entry was bound from configuration, logged as seeded, documented in the
/// runbook — and then dropped, because the live snapshot is loaded from the database rather than
/// from appsettings. These tests exercise the path that failed rather than the one that did not.
/// </remarks>
public sealed class ConfiguredRateLimitSeedIntegrationTests
{
    private const string AdminKey = "sk-33pol-configured-rl-admin";

    [Fact]
    public async Task ConfiguredScopedRules_AppearInTheAdminViewOfTheDatabase()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var get = await admin.GetAsync("/admin/api/rate-limits");
        get.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var rules = json.RootElement.GetProperty("rules").EnumerateArray()
            .Select(r => (
                Scope: r.GetProperty("scope").GetString(),
                Target: r.GetProperty("target").GetString(),
                Rpm: r.GetProperty("rpm").GetInt32()))
            .ToList();

        rules.Should().Contain(("model", "local-mock", 100));
        rules.Should().Contain(("tenant_model", "default|local-mock", 2));
        rules.Should().Contain(("auth_failure", "*", 30));
    }

    /// <summary>
    /// The tenant scopes are keyed on the tenant id — a GUID — but an operator writes the slug they
    /// know the customer by. That rule was previously accepted, persisted, listed by the admin API
    /// and never matched anything; the earlier version of this test asserted only that it came back
    /// on a GET, which a rule that can never fire also does.
    /// </summary>
    [Fact]
    public async Task AConfiguredTenantModelRule_WrittenAgainstTheTenantSlug_IsEnforced()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var client = await CreateInferenceClientAsync(factory, admin, granted: true);

        // The tenant|model rule is 2 rpm and the model rule is 100, so the tenant rule is the one
        // that must bite — and only if "default" resolved to the bootstrapped tenant.
        (await PostChatAsync(client)).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);
        (await PostChatAsync(client)).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);

        var refused = await PostChatAsync(client);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.GetValues("X-33pol-RateLimit-Scope").Single().Should().Be("tenant_model");
    }

    [Fact]
    public async Task AConfiguredPerModelRule_IsEnforcedOnInference()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler, modelRpm: 1);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var client = await CreateInferenceClientAsync(factory, admin, granted: true);

        (await PostChatAsync(client)).StatusCode.Should()
            .BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);

        var refused = await PostChatAsync(client);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.GetValues("X-33pol-RateLimit-Scope").Single().Should().Be("model");
    }

    /// <summary>
    /// The <c>model</c> bucket is shared by everyone who calls that model, and grants are checked
    /// downstream. Charging it before the grant meant a key with no access at all could drain it —
    /// so a single ungranted caller could deny the model to every tenant that <em>was</em> granted
    /// it, at nothing more than its own request rate.
    /// </summary>
    [Fact]
    public async Task AnUngrantedKey_CannotDrainTheSharedModelBudget()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler, modelRpm: 1);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var intruder = await CreateInferenceClientAsync(factory, admin, granted: false);
        var legitimate = await CreateInferenceClientAsync(factory, admin, granted: true);

        for (var i = 0; i < 20; i++)
        {
            var denied = await PostChatAsync(intruder);
            denied.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "the key was never granted this model");
        }

        var served = await PostChatAsync(legitimate);

        served.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.BadGateway);
        served.StatusCode.Should().NotBe(
            HttpStatusCode.TooManyRequests,
            "a caller with no access to the model must not be able to spend its budget");
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = "local-mock",
            messages = new[] { new { role = "user", content = "hi" } },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    private static async Task<HttpClient> CreateInferenceClientAsync(
        WebApplicationFactory<Program> factory,
        HttpClient admin,
        bool granted)
    {
        var createKey = await admin.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createKey.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createKey.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        if (granted)
        {
            var grant = await admin.PutAsJsonAsync(
                $"/admin/api/keys/{keyId}/model-grants",
                new { modelIds = new[] { "local-mock" } });
            grant.EnsureSuccessStatusCode();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    /// <summary>
    /// Every scope is configured in appsettings and nowhere else — no admin write happens in these
    /// tests, which is the whole point. Each test makes the rule it is about the tightest one, so a
    /// rule that fails to reach the database (or reaches it and never matches) shows up as a request
    /// being admitted rather than as a missing row nobody looks at.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        HttpMessageHandler? upstreamHandler = null,
        int modelRpm = 100)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-configured-rl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "appsettings.json"),
            """
            {
              "RateLimiting": {
                "Default": { "Rpm": 10000, "Burst": 0, "MaxConcurrentStreams": 100 },
                "Plans": {},
                "Models": {
                  "local-mock": { "Rpm": __MODEL_RPM__, "Burst": 0, "MaxConcurrentStreams": 0 }
                },
                "TenantModels": {
                  "default|local-mock": { "Rpm": 2, "Burst": 0, "MaxConcurrentStreams": 0 }
                },
                "AuthFailure": { "Rpm": 30, "Burst": 0, "MaxConcurrentStreams": 0 }
              },
              "Gateway": {
                "Bootstrap": { "Enabled": false },
                "ModelsConfigPath": "config/models.json"
              }
            }
            """.Replace("__MODEL_RPM__", modelRpm.ToString()));

        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: upstreamHandler,
            configureSettings: settings => settings["Gateway:AppSettingsPath"] = "appsettings.json")
            .WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
