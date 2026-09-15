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
/// Switching a rule off, end to end. The point of the flag is that it is not a delete: the rule stops
/// being enforced but keeps its tier and its windows and stays in the admin's list, so an operator
/// who turns a limit off during an incident can turn it back on without re-authoring it.
/// </summary>
public sealed class AdminRateLimitRuleEnabledEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// The whole contract in one pass: enforced, then switched off and not enforced, then switched
    /// back on and enforced again — with the tier and the windows surviving the round trip.
    /// </summary>
    [Fact]
    public async Task ADisabledRule_StopsBeingEnforcedButKeepsItsTierAndWindows()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        // A window far in the future, so the base tier is what applies during the test whatever time
        // of day it runs at — a recurring window would make the assertions depend on the clock.
        var soon = DateTimeOffset.UtcNow.AddDays(30);
        object Rule(bool enabled) => new
        {
            scope = "model",
            target = "local-mock",
            rpm = 1,
            burst = 0,
            maxConcurrentStreams = 0,
            enabled,
            schedule = new[]
            {
                new { name = "quiet-hours", kind = "once", rpm = 5, burst = 0, maxConcurrentStreams = 0, from = soon, until = soon.AddDays(1) },
            },
        };

        async Task PutAsync(bool enabled)
        {
            var put = await admin.PutAsJsonAsync(
                "/admin/api/rate-limits",
                new
                {
                    enabled = true,
                    @default = new { rpm = 10_000, burst = 0, maxConcurrentStreams = 100 },
                    plans = new Dictionary<string, object>(),
                    rules = new[] { Rule(enabled) },
                });
            put.EnsureSuccessStatusCode();
        }

        // Enforced: the 1 rpm model rule bites on the second request.
        await PutAsync(enabled: true);
        var client = await CreateInferenceClientAsync(factory, admin);
        (await PostChatAsync(client)).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);
        (await PostChatAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Switched off: the same rule no longer refuses anything.
        await PutAsync(enabled: false);
        for (var i = 0; i < 3; i++)
        {
            (await PostChatAsync(client)).StatusCode
                .Should().BeOneOf(
                    [HttpStatusCode.OK, HttpStatusCode.BadGateway],
                    "a switched-off rule enforces nothing");
        }

        // ...but it is still there, with its numbers and its window, ready to come back.
        var get = await admin.GetAsync("/admin/api/rate-limits");
        get.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var rule = json.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("target").GetString() == "local-mock");
        rule.GetProperty("enabled").GetBoolean().Should().BeFalse();
        rule.GetProperty("rpm").GetInt32().Should().Be(1);
        rule.GetProperty("schedule").EnumerateArray().Single()
            .GetProperty("name").GetString().Should().Be("quiet-hours");

        // Switched back on: enforced again, without having been re-authored. The bucket is still
        // empty from the first phase — re-enabling resumes a rule rather than granting it a fresh
        // allowance — so the very next request is refused, which is the proof enforcement is back.
        await PutAsync(enabled: true);
        (await PostChatAsync(client)).StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "the rule is enforced again, against the same bucket it was refusing from before");
    }

    /// <summary>
    /// A client written before the flag existed keeps writing enforced rules. Were the default false,
    /// an older console round-tripping the configuration would silently switch off every limit.
    /// </summary>
    [Fact]
    public async Task ARuleWithoutTheEnabledField_IsEnforced()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 50, burst = 5, maxConcurrentStreams = 0 },
                },
            });
        put.EnsureSuccessStatusCode();

        var get = await admin.GetAsync("/admin/api/rate-limits");
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("rules").EnumerateArray().Single()
            .GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// A switched-off rule enforces nothing, so it has nothing to say about what is in force or when
    /// that changes. Leaving it in the report would draw windows that can never take effect.
    /// </summary>
    [Fact]
    public async Task ADisabledRule_IsAbsentFromTheScheduleReport()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var now = DateTimeOffset.UtcNow;
        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new object[]
                {
                    new
                    {
                        scope = "model",
                        target = "switched-off",
                        rpm = 50,
                        burst = 5,
                        maxConcurrentStreams = 0,
                        enabled = false,
                        schedule = new[]
                        {
                            new { name = "launch", kind = "once", rpm = 500, burst = 50, maxConcurrentStreams = 0, from = now.AddHours(-1), until = now.AddHours(1) },
                        },
                    },
                    new { scope = "model", target = "still-on", rpm = 50, burst = 5, maxConcurrentStreams = 0, enabled = true },
                },
            });
        put.EnsureSuccessStatusCode();

        var response = await admin.GetAsync("/admin/api/rate-limits/schedule");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var targets = json.RootElement.GetProperty("rules").EnumerateArray()
            .Select(r => r.GetProperty("target").GetString())
            .ToList();
        targets.Should().Contain("still-on");
        targets.Should().NotContain("switched-off");

        json.RootElement.GetProperty("occurrences").EnumerateArray()
            .Should().NotContain(o => o.GetProperty("target").GetString() == "switched-off");
    }

    /// <summary>
    /// Switching a rule off must not move it in the list: an operator should see it stay put and go
    /// quiet, and a diff of two GETs should show one changed field rather than a reordering.
    /// </summary>
    [Fact]
    public async Task SwitchingARuleOff_DoesNotReorderTheList()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        async Task<List<string?>> PutAndListAsync(bool middleEnabled)
        {
            var put = await admin.PutAsJsonAsync(
                "/admin/api/rate-limits",
                new
                {
                    enabled = true,
                    @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                    plans = new Dictionary<string, object>(),
                    rules = new[]
                    {
                        new { scope = "model", target = "aaa", rpm = 10, burst = 0, maxConcurrentStreams = 0, enabled = true },
                        new { scope = "model", target = "mmm", rpm = 20, burst = 0, maxConcurrentStreams = 0, enabled = middleEnabled },
                        new { scope = "model", target = "zzz", rpm = 30, burst = 0, maxConcurrentStreams = 0, enabled = true },
                    },
                });
            put.EnsureSuccessStatusCode();

            var get = await admin.GetAsync("/admin/api/rate-limits");
            using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("rules").EnumerateArray()
                .Select(r => r.GetProperty("target").GetString())
                .ToList();
        }

        var whileOn = await PutAndListAsync(middleEnabled: true);
        var whileOff = await PutAndListAsync(middleEnabled: false);

        whileOn.Should().Equal("aaa", "mmm", "zzz");
        whileOff.Should().Equal(whileOn);
    }

    /// <summary>
    /// A disabled rule's tier is validated exactly as an enforced one's is, so switching it back on
    /// cannot restore a configuration that was never checked — the same reason the tier numbers are
    /// validated when rate limiting is disabled wholesale.
    /// </summary>
    [Fact]
    public async Task ADisabledRule_IsStillValidated()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 0, burst = 0, maxConcurrentStreams = 0, enabled = false },
                },
            });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static WebApplicationFactory<Program> CreateFactory(HttpMessageHandler? upstreamHandler = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-rl-enabled-" + Guid.NewGuid().ToString("N"));
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
}
