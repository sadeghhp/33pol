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
/// Scoped rules end to end: configured through the admin API, persisted, reloaded into the live
/// snapshot, and enforced on the inference path — plus the usage report that shows what they did.
/// </summary>
public sealed class AdminScopedRateLimitEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// A per-model rule configured through the API reaches the request path without a restart, and
    /// binds even though the tenant is nowhere near its own far larger tier.
    /// </summary>
    [Fact]
    public async Task PutRateLimits_WithAPerModelRule_IsEnforcedOnInference()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 10_000, burst = 0, maxConcurrentStreams = 100 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 1, burst = 0, maxConcurrentStreams = 0 },
                },
            });
        put.EnsureSuccessStatusCode();

        var client = await CreateInferenceClientAsync(factory, admin);

        var first = await PostChatAsync(client);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);

        var second = await PostChatAsync(client);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.GetValues("X-33pol-RateLimit-Scope").Single().Should().Be("model");
    }

    /// <summary>A rule survives the round trip through the database and comes back on the next GET.</summary>
    [Fact]
    public async Task PutRateLimits_RulesRoundTripThroughTheDatabase()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                adaptiveEnabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 50, burst = 5, maxConcurrentStreams = 0 },
                    new { scope = "tenant_model", target = "acme|local-mock", rpm = 20, burst = 0, maxConcurrentStreams = 2 },
                },
            });
        put.EnsureSuccessStatusCode();

        var get = await admin.GetAsync("/admin/api/rate-limits");
        get.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("adaptiveEnabled").GetBoolean().Should().BeTrue();

        var rules = json.RootElement.GetProperty("rules").EnumerateArray().ToList();
        rules.Should().HaveCount(2);
        rules.Should().Contain(r =>
            r.GetProperty("scope").GetString() == "tenant_model" &&
            r.GetProperty("target").GetString() == "acme|local-mock" &&
            r.GetProperty("rpm").GetInt32() == 20);
    }

    /// <summary>
    /// A client written against the older contract sends no rules at all. That must leave the stored
    /// rules alone — a client that cannot see them must not be able to delete them by omission.
    /// </summary>
    [Fact]
    public async Task PutRateLimits_WithoutARulesField_KeepsTheStoredRules()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var withRules = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 50, burst = 5, maxConcurrentStreams = 0 },
                },
            });
        withRules.EnsureSuccessStatusCode();

        var legacyClient = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 200, burst = 20, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
            });
        legacyClient.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await (await admin.GetAsync("/admin/api/rate-limits")).Content.ReadAsStringAsync());
        json.RootElement.GetProperty("default").GetProperty("rpm").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("rules").GetArrayLength().Should()
            .Be(1, "the rule the older client could not see must survive its write");
    }

    /// <summary>An empty array is a deliberate "there are no rules", and does delete them.</summary>
    [Fact]
    public async Task PutRateLimits_WithAnEmptyRulesArray_DeletesTheStoredRules()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 50, burst = 5, maxConcurrentStreams = 0 },
                },
            });

        var cleared = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = Array.Empty<object>(),
            });
        cleared.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await (await admin.GetAsync("/admin/api/rate-limits")).Content.ReadAsStringAsync());
        json.RootElement.GetProperty("rules").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("nonsense", "local-mock", "scope")]
    [InlineData("tenant_model", "no-separator", "pair")]
    [InlineData("global", "not-a-star", "single partition")]
    public async Task PutRateLimits_WithAMalformedRule_Returns400(string scope, string target, string expected)
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var response = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[] { new { scope, target, rpm = 10, burst = 0, maxConcurrentStreams = 0 } },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("message").GetString().Should().Contain(expected);
    }

    /// <summary>
    /// The report is what an operator reads during an incident: who is sending what, against which
    /// limit, and where the refusals landed.
    /// </summary>
    [Fact]
    public async Task GetUsage_AfterTraffic_ReportsPerModelLoadAndTheViolationsThatFollowed()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 10_000, burst = 0, maxConcurrentStreams = 100 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 1, burst = 0, maxConcurrentStreams = 0 },
                },
            });

        var client = await CreateInferenceClientAsync(factory, admin);
        for (var i = 0; i < 4; i++)
        {
            await PostChatAsync(client);
        }

        var usage = await admin.GetAsync("/admin/api/rate-limits/usage?minutes=60&take=25");
        usage.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await usage.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("totals").GetProperty("requests").GetInt64().Should().BeGreaterThanOrEqualTo(4);
        json.RootElement.GetProperty("totals").GetProperty("rejected").GetInt64().Should().BeGreaterThan(0);

        var byModel = json.RootElement.GetProperty("byModel").EnumerateArray().ToList();
        byModel.Should().Contain(r => r.GetProperty("key").GetString() == "local-mock");

        var violations = json.RootElement.GetProperty("violations").EnumerateArray().ToList();
        violations.Should().Contain(v =>
            v.GetProperty("scope").GetString() == "model" &&
            v.GetProperty("key").GetString() == "local-mock");
    }

    [Fact]
    public async Task GetUsage_WithoutAuth_Returns401()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var response = await factory.CreateClient().GetAsync("/admin/api/rate-limits/usage");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        HttpClient admin)
    {
        var createKey = await admin.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createKey.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createKey.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var grant = await admin.PutAsJsonAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new { modelIds = new[] { "local-mock" } });
        grant.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory(HttpMessageHandler? upstreamHandler = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-scoped-rl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "appsettings.json"),
            """
            {
              "RateLimiting": {
                "Default": { "Rpm": 10000, "Burst": 100, "MaxConcurrentStreams": 100 },
                "Plans": {}
              },
              "Gateway": {
                "Bootstrap": { "Enabled": false },
                "ModelsConfigPath": "config/models.json"
              }
            }
            """);

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
