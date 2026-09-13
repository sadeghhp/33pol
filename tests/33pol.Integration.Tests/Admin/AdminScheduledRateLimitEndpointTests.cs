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
/// Scheduled windows end to end: saved through the admin API, stored as part of the rule, projected
/// into the live snapshot, enforced on the inference path, and described by the schedule report.
/// </summary>
public sealed class AdminScheduledRateLimitEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// A window that is active right now is what the request path enforces, while the admin GET
    /// keeps showing the base tier the operator configured.
    /// </summary>
    [Fact]
    public async Task PutRateLimits_WithAnActiveWindow_EnforcesTheWindowTier()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactory(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var now = DateTimeOffset.UtcNow;
        var put = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 10_000, burst = 0, maxConcurrentStreams = 100 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new
                    {
                        scope = "model",
                        target = "local-mock",
                        rpm = 5_000,
                        burst = 0,
                        maxConcurrentStreams = 0,
                        schedule = new[]
                        {
                            new { name = "tight", kind = "once", rpm = 1, burst = 0, maxConcurrentStreams = 0, from = now.AddHours(-1), until = now.AddHours(1) },
                        },
                    },
                },
            });
        put.EnsureSuccessStatusCode();

        var get = await admin.GetAsync("/admin/api/rate-limits");
        using (var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync()))
        {
            var rule = json.RootElement.GetProperty("rules").EnumerateArray().Single();
            rule.GetProperty("rpm").GetInt32().Should().Be(5_000, "the admin API shows the base tier");
            rule.GetProperty("schedule").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("tight");
        }

        var client = await CreateInferenceClientAsync(factory, admin);

        var first = await PostChatAsync(client);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);
        first.Headers.GetValues("X-33pol-RateLimit-Limit").Single().Should().Be("1", "the window's tier is in force");

        var second = await PostChatAsync(client);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.GetValues("X-33pol-RateLimit-Scope").Single().Should().Be("model");
    }

    [Fact]
    public async Task GetSchedule_ReportsEffectiveTiersOccurrencesAndTransitions()
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
                rules = new[]
                {
                    new
                    {
                        scope = "model",
                        target = "local-mock",
                        rpm = 50,
                        burst = 5,
                        maxConcurrentStreams = 0,
                        schedule = new[]
                        {
                            new { name = "launch", kind = "once", rpm = 500, burst = 50, maxConcurrentStreams = 0, from = now.AddHours(-2), until = now.AddHours(2) },
                        },
                    },
                },
            });
        put.EnsureSuccessStatusCode();

        var from = now.AddHours(-1);
        var response = await admin.GetAsync(
            $"/admin/api/rate-limits/schedule?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(from.AddDays(7).ToString("O"))}&take=1");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rule = json.RootElement.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("target").GetString() == "local-mock");
        rule.GetProperty("activeWindow").GetString().Should().Be("launch");
        rule.GetProperty("effective").GetProperty("rpm").GetInt32().Should().Be(500);
        rule.GetProperty("base").GetProperty("rpm").GetInt32().Should().Be(50);

        var occurrence = json.RootElement.GetProperty("occurrences").EnumerateArray().Single();
        occurrence.GetProperty("window").GetString().Should().Be("launch");
        occurrence.GetProperty("clippedStart").GetBoolean().Should().BeTrue("the window began before the calendar range");
        occurrence.GetProperty("start").GetDateTimeOffset().Should().BeCloseTo(from, TimeSpan.FromSeconds(1));

        json.RootElement.GetProperty("transitions").EnumerateArray().Should().ContainSingle();
        json.RootElement.GetProperty("transitionsTotal").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("transitionsTruncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetSchedule_AcceptsAWallClockInstantInAZone()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        // 1 October 2026 11:30 in Berlin is 09:30Z.
        var response = await admin.GetAsync("/admin/api/rate-limits/schedule?atLocal=2026-10-01T11:30&timeZone=Europe/Berlin");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("at").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));

        var bad = await admin.GetAsync("/admin/api/rate-limits/schedule?atLocal=2026-10-01T11:30&timeZone=Nowhere/Land");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutRateLimits_WithOverlappingWindows_IsRejected()
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
                    new
                    {
                        scope = "model",
                        target = "local-mock",
                        rpm = 50,
                        burst = 5,
                        maxConcurrentStreams = 0,
                        schedule = new object[]
                        {
                            new { name = "off-peak", kind = "weekly", rpm = 100, burst = 0, maxConcurrentStreams = 0, days = new[] { "mon", "tue", "wed", "thu", "fri" }, start = "19:00", end = "07:00", timeZone = "Europe/Berlin" },
                            new { name = "weekend", kind = "weekly", rpm = 150, burst = 0, maxConcurrentStreams = 0, days = new[] { "sat", "sun" }, start = "00:00", end = "24:00", timeZone = "Europe/Berlin" },
                        },
                    },
                },
            });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadAsStringAsync();
        body.Should().Contain("off-peak").And.Contain("weekend");
    }

    /// <summary>
    /// A client that sends a rule without a schedule field keeps whatever windows are stored for
    /// that rule: a client that cannot see windows must not be able to delete them by omission.
    /// </summary>
    [Fact]
    public async Task PutRateLimits_RuleWithoutAScheduleField_KeepsTheStoredWindows()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var first = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new
                    {
                        scope = "model",
                        target = "local-mock",
                        rpm = 50,
                        burst = 5,
                        maxConcurrentStreams = 0,
                        schedule = new[]
                        {
                            new { name = "nightly", kind = "weekly", rpm = 100, burst = 0, maxConcurrentStreams = 0, days = new[] { "mon" }, start = "22:00", end = "04:00", timeZone = "UTC" },
                        },
                    },
                },
            });
        first.EnsureSuccessStatusCode();

        var second = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 60, burst = 5, maxConcurrentStreams = 0 },
                },
            });
        second.EnsureSuccessStatusCode();

        var get = await admin.GetAsync("/admin/api/rate-limits");
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var rule = json.RootElement.GetProperty("rules").EnumerateArray().Single();
        rule.GetProperty("rpm").GetInt32().Should().Be(60);
        rule.GetProperty("schedule").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("nightly");

        var third = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "local-mock", rpm = 60, burst = 5, maxConcurrentStreams = 0, schedule = Array.Empty<object>() },
                },
            });
        third.EnsureSuccessStatusCode();

        var after = await admin.GetAsync("/admin/api/rate-limits");
        using var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        afterJson.RootElement.GetProperty("rules").EnumerateArray().Single().GetProperty("schedule").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PreviewWindow_ReportsOverlapAndPrecedenceWithoutSaving()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var response = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/windows/preview",
            new
            {
                scope = "model",
                target = "gpt-4",
                rpm = 600,
                burst = 60,
                maxConcurrentStreams = 40,
                candidate = "weekend",
                windows = new object[]
                {
                    new { name = "off-peak", kind = "weekly", rpm = 1200, burst = 200, maxConcurrentStreams = 80, days = new[] { "mon", "tue", "wed", "thu", "fri" }, start = "19:00", end = "07:00", timeZone = "Europe/Berlin" },
                    new { name = "launch", kind = "once", rpm = 3000, burst = 500, maxConcurrentStreams = 120, from = "2036-10-01T00:00:00Z", until = "2036-10-03T00:00:00Z" },
                    new { name = "weekend", kind = "weekly", rpm = 1500, burst = 300, maxConcurrentStreams = 100, days = new[] { "sat", "sun" }, start = "00:00", end = "24:00", timeZone = "Europe/Berlin" },
                },
            });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("overlaps").EnumerateArray().Single().GetString().Should().Be("off-peak");
        json.RootElement.GetProperty("outrankedBy").EnumerateArray().Single().GetString().Should().Be("launch");
        // On a weekend the candidate is running right now, and "next" is the occurrence in progress.
        var activeNow = json.RootElement.GetProperty("activeNow").GetBoolean();
        var nextStart = json.RootElement.GetProperty("nextStartAt").GetDateTimeOffset();
        if (activeNow)
        {
            nextStart.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
        }
        else
        {
            nextStart.Should().BeAfter(DateTimeOffset.UtcNow);
        }

        var get = await admin.GetAsync("/admin/api/rate-limits");
        using var stored = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        stored.RootElement.GetProperty("rules").EnumerateArray().Should().NotContain(r => r.GetProperty("target").GetString() == "gpt-4", "a preview persists nothing");
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
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-scheduled-rl-" + Guid.NewGuid().ToString("N"));
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
