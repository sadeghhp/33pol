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

    /// <summary>
    /// A tenant rule with rpm 0 caps that tenant's streams and leaves its rate to the default tier.
    /// It used to be floored to 1 rpm, so the second request in a minute was refused.
    /// </summary>
    [Fact]
    public async Task PutRateLimits_WithAConcurrencyOnlyTenantRule_LeavesTheTenantRateAlone()
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
                    // "default" is the bootstrap tenant's slug.
                    new { scope = "tenant", target = "default", rpm = 0, burst = 0, maxConcurrentStreams = 2 },
                },
            });
        put.EnsureSuccessStatusCode();

        var client = await CreateInferenceClientAsync(factory, admin);

        for (var i = 0; i < 5; i++)
        {
            var response = await PostChatAsync(client);
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);
            response.Headers.GetValues("X-33pol-RateLimit-Limit").Single().Should()
                .Be("10000", "the tenant rule inherits the default tier's rate rather than being floored to 1 rpm");
        }
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

    /// <summary>
    /// The per-limit section, end to end through the real limiter: which configured control was
    /// asked, which one refused, and which one had to give its token back.
    /// </summary>
    /// <remarks>
    /// The seam this covers is the one the unit tests cannot: the middleware records against a real
    /// tracker, and the ids it stamps have to be the same strings the admin API reports rules under.
    /// A join that works on a recording fake but not on the real report would look exactly like a
    /// page with no activity.
    /// </remarks>
    [Fact]
    public async Task GetUsage_AfterTraffic_AttributesEachDecisionToTheLimitThatMadeIt()
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

        using var json = JsonDocument.Parse(
            await (await admin.GetAsync("/admin/api/rate-limits/usage?minutes=60&take=25")).Content.ReadAsStringAsync());
        var limits = json.RootElement.GetProperty("limits").EnumerateArray().ToArray();

        // Every row, whatever it saw: a limit that is asked either keeps its token, is the one that
        // refused, or gives it back. No fourth case, and nothing counted twice.
        limits.Should().NotBeEmpty().And.OnlyContain(l =>
            l.GetProperty("evaluations").GetInt64()
                == l.GetProperty("charged").GetInt64()
                   + l.GetProperty("refusedByRate").GetInt64()
                   + l.GetProperty("passedThenRefunded").GetInt64());

        // The model rule is the one that refused, and it is reported under the identity the rules
        // API stores it as — scope:target, which is what the console joins a row by.
        var model = limits.Single(l => l.GetProperty("limitId").GetString() == "model:local-mock");
        model.GetProperty("scope").GetString().Should().Be("model");
        model.GetProperty("target").GetString().Should().Be("local-mock");
        model.GetProperty("charged").GetInt64().Should().Be(1);
        model.GetProperty("refusedByRate").GetInt64().Should().Be(3);
        model.GetProperty("passedThenRefunded").GetInt64().Should().Be(0);
        model.GetProperty("singleBucket").GetBoolean().Should().BeTrue();
        model.GetProperty("effectiveRpm").GetInt32().Should().Be(1);

        // The tenant scope admitted all four — it is nowhere near its own tier — and handed three
        // tokens back when the model limit refused. Counting those as charged would show load the
        // tenant's bucket never carried.
        var tier = limits.Single(l => l.GetProperty("scope").GetString() is "default" or "plan");
        tier.GetProperty("evaluations").GetInt64().Should().Be(4);
        tier.GetProperty("charged").GetInt64().Should().Be(1);
        tier.GetProperty("refusedByRate").GetInt64().Should().Be(0);
        tier.GetProperty("passedThenRefunded").GetInt64().Should().Be(3);
        tier.GetProperty("singleBucket").GetBoolean().Should().BeFalse("a tier gives every tenant a bucket of its own");
        tier.GetProperty("peakUtilization").ValueKind.Should().Be(JsonValueKind.Null);

        json.RootElement.GetProperty("tracker").GetProperty("isSaturated").GetBoolean().Should().BeFalse();
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
