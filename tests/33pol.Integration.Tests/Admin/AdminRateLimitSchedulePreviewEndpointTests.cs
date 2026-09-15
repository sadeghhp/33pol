using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The schedule report for a rule set that has not been saved. The console's calendar exists to
/// answer "did I get this schedule right?", and the stored configuration cannot answer that for a
/// change still being composed — so the same report is computed over rules supplied in the body.
/// Nothing here may touch what is stored.
/// </summary>
public sealed class AdminRateLimitSchedulePreviewEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// The point of the route: the answer follows the submitted rules, not the saved ones, while the
    /// saved configuration is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task PreviewSchedule_AnswersForTheSubmittedRules_AndPersistsNothing()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var now = DateTimeOffset.UtcNow;
        var saved = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = new[]
                {
                    new { scope = "model", target = "saved-only", rpm = 50, burst = 5, maxConcurrentStreams = 0 },
                },
            });
        saved.EnsureSuccessStatusCode();

        // A draft that renames the rule and puts an active window on it — neither of which is stored.
        var response = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new
            {
                rules = new[]
                {
                    new
                    {
                        scope = "model",
                        target = "drafted-only",
                        rpm = 50,
                        burst = 5,
                        maxConcurrentStreams = 0,
                        schedule = new[]
                        {
                            new { name = "launch", kind = "once", rpm = 500, burst = 50, maxConcurrentStreams = 0, from = now.AddHours(-1), until = now.AddHours(1) },
                        },
                    },
                },
                from = now.AddMinutes(-30),
                to = now.AddDays(7),
                take = 10,
            });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rules = json.RootElement.GetProperty("rules").EnumerateArray().ToList();
        rules.Should().ContainSingle().Which.GetProperty("target").GetString().Should().Be("drafted-only");
        rules[0].GetProperty("activeWindow").GetString().Should().Be("launch");
        rules[0].GetProperty("effective").GetProperty("rpm").GetInt32().Should().Be(500);

        // The stored configuration is untouched: still the saved rule, still with no windows.
        var after = await admin.GetAsync("/admin/api/rate-limits");
        after.EnsureSuccessStatusCode();
        using var stored = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        var storedRules = stored.RootElement.GetProperty("rules").EnumerateArray().ToList();
        storedRules.Should().ContainSingle().Which.GetProperty("target").GetString().Should().Be("saved-only");
        storedRules[0].GetProperty("schedule").EnumerateArray().Should().BeEmpty();
    }

    /// <summary>
    /// An empty set means "no scoped rules", never "fall back to what is stored" — substituting the
    /// saved rules would answer a question the caller did not ask, and would do it invisibly.
    /// </summary>
    [Fact]
    public async Task PreviewSchedule_WithNoRules_DoesNotFallBackToTheStoredSet()
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

        var response = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = Array.Empty<object>() });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("rules").EnumerateArray().Should().BeEmpty();

        // A missing rules field reads the same way, for the same reason.
        var omitted = await admin.PostAsJsonAsync("/admin/api/rate-limits/schedule/preview", new { });
        omitted.EnsureSuccessStatusCode();
        using var omittedJson = JsonDocument.Parse(await omitted.Content.ReadAsStringAsync());
        omittedJson.RootElement.GetProperty("rules").EnumerateArray().Should().BeEmpty();
    }

    /// <summary>
    /// A draft is held to exactly what a save holds it to, and refused in the same words — so an
    /// operator learns the schedule is invalid while the drawer that owns the mistake is still open.
    /// </summary>
    [Fact]
    public async Task PreviewSchedule_WithADraftTheSaveWouldRefuse_IsRefusedTheSameWay()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var now = DateTimeOffset.UtcNow;
        var overlapping = new[]
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
                    new { name = "a", kind = "once", rpm = 10, burst = 0, maxConcurrentStreams = 0, from = now, until = now.AddHours(4) },
                    new { name = "b", kind = "once", rpm = 20, burst = 0, maxConcurrentStreams = 0, from = now.AddHours(1), until = now.AddHours(3) },
                },
            },
        };

        var preview = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = overlapping });
        preview.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var previewMessage = JsonDocument.Parse(await preview.Content.ReadAsStringAsync())
            .RootElement.GetProperty("message").GetString();

        var save = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                enabled = true,
                @default = new { rpm = 100, burst = 10, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
                rules = overlapping,
            });
        save.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var saveMessage = JsonDocument.Parse(await save.Content.ReadAsStringAsync())
            .RootElement.GetProperty("message").GetString();

        previewMessage.Should().NotBeNullOrWhiteSpace();
        previewMessage.Should().Be(saveMessage, "a draft refused by the preview must be refused by the save for the same stated reason");
    }

    /// <summary>
    /// The range rules are shared with the GET rather than reimplemented, so the two routes cannot
    /// disagree about what a valid window is while claiming to answer the same question.
    /// </summary>
    [Theory]
    [InlineData(-1, "to must be after from.")]
    [InlineData(63, "The calendar range may not exceed 62 days.")]
    public async Task PreviewSchedule_AppliesTheSameRangeRulesAsTheStoredRoute(int days, string expected)
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(days);

        var preview = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = Array.Empty<object>(), from, to });
        preview.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await preview.Content.ReadAsStringAsync())
            .RootElement.GetProperty("message").GetString().Should().Be(expected);

        var stored = await admin.GetAsync(
            $"/admin/api/rate-limits/schedule?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");
        stored.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await stored.Content.ReadAsStringAsync())
            .RootElement.GetProperty("message").GetString().Should().Be(expected);
    }

    /// <summary>The wall-clock spelling the operator types resolves the same way it does on the GET.</summary>
    [Fact]
    public async Task PreviewSchedule_AcceptsAWallClockInstantInAZone()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var response = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = Array.Empty<object>(), atLocal = "2026-10-01T11:30", timeZone = "Europe/Berlin", take = 1 });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("at").GetDateTimeOffset()
            .Should().Be(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));

        var bad = await admin.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = Array.Empty<object>(), atLocal = "2026-10-01T11:30", timeZone = "Nowhere/Land" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A preview reads and computes, but it is still an operator surface.</summary>
    [Fact]
    public async Task PreviewSchedule_WithoutAuth_Returns401()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/admin/api/rate-limits/schedule/preview",
            new { rules = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-rl-schedule-preview-" + Guid.NewGuid().ToString("N"));
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
