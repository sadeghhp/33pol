using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The read side of the rate-limit admin API that says how far to trust what it shows: whether a
/// save can work, whether the counters are complete, what each limit did, and what changed.
/// </summary>
public sealed class AdminRateLimitObservabilityEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    // --- Writable -------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithADatabase_SaysWritable()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits");

        json.GetProperty("writable").GetBoolean().Should().BeTrue();
        json.GetProperty("readOnlyReason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The GET and the PUT have to agree, because the console disables editing on the first and an
    /// operator finds out the hard way on the second. Both look for the same repository.
    /// </summary>
    [Fact]
    public async Task Get_WithoutASettingsStore_SaysReadOnly_AndThePutAgrees()
    {
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.RemoveAll<IRateLimitSettingsRepository>()));
        var client = await ClientAsync(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits");
        var put = await client.PutAsJsonAsync("/admin/api/rate-limits", ValidBody());

        json.GetProperty("writable").GetBoolean().Should().BeFalse();
        json.GetProperty("readOnlyReason").GetString().Should().Be("store_unavailable");
        json.GetProperty("default").GetProperty("rpm").GetInt32().Should().BeGreaterThan(0, "inspection still works");
        put.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// The page an operator opens to look at limits they cannot change must not be the page that
    /// fails when the database is unhappy. Registration is what this reports, so it is asked of the
    /// container — resolving the repository would build a DbContext on every read, and a failure to
    /// build one would turn a look at the configuration into a 500.
    /// </summary>
    [Fact]
    public async Task Get_WhenTheSettingsStoreCannotBeConstructed_StillLoadsTheConfiguration()
    {
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRateLimitSettingsRepository>();
                services.AddScoped<IRateLimitSettingsRepository>(
                    _ => throw new InvalidOperationException("the database is not reachable"));
            }));
        var client = await ClientAsync(factory);

        var response = await client.GetAsync("/admin/api/rate-limits");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("default").GetProperty("rpm").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("rules").ValueKind.Should().Be(JsonValueKind.Array);

        // Registered, so it is reported as writable; the save is where the failure belongs and is
        // reported, rather than being guessed at from a read.
        json.GetProperty("writable").GetBoolean().Should().BeTrue();
        ((int)(await client.PutAsJsonAsync("/admin/api/rate-limits", ValidBody())).StatusCode)
            .Should().BeGreaterThanOrEqualTo(500, "the save is where a broken store shows up, not the read");
    }

    [Fact]
    public async Task Put_IgnoresWritableEchoedBackInTheBody()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var response = await client.PutAsJsonAsync("/admin/api/rate-limits", new
        {
            enabled = true,
            @default = new { rpm = 50, burst = 5, maxConcurrentStreams = 2 },
            plans = new Dictionary<string, object>(),
            writable = false,
            readOnlyReason = "store_unavailable",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Usage report ---------------------------------------------------------------------------

    [Fact]
    public async Task Usage_CarriesTrackerCompleteness_BothProtectiveRows_AndLimits()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var tracker = factory.Services.GetRequiredService<IRateLimitUsageTracker>();
        tracker.RecordRateStage(
            [new RateLimitRule(RateLimitScope.Tenant, "t:acme", new RateLimitPolicy(120, 0, 0)) { LimitId = "tenant:acme" }],
            RateLimitStageOutcome.Charged);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Refused, 30);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/usage?minutes=5");

        var trackerJson = json.GetProperty("tracker");
        trackerJson.GetProperty("isSaturated").GetBoolean().Should().BeFalse();
        trackerJson.GetProperty("maxKeysPerDimension").GetInt32().Should().BeGreaterThan(0);
        trackerJson.GetProperty("trackingSinceUtc").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
        trackerJson.GetProperty("dimensions").EnumerateArray().Select(d => d.GetProperty("name").GetString())
            .Should().Equal("tenants", "models", "apiKeys", "tenantModels", "violations", "limits");

        var limit = json.GetProperty("limits").EnumerateArray().Single(l => l.GetProperty("limitId").GetString() == "tenant:acme");
        limit.GetProperty("scope").GetString().Should().Be("tenant");
        limit.GetProperty("target").GetString().Should().Be("acme");
        limit.GetProperty("charged").GetInt64().Should().Be(1);
        limit.GetProperty("singleBucket").GetBoolean().Should().BeTrue();
        limit.GetProperty("effectiveRpm").GetInt32().Should().Be(120);

        var protective = json.GetProperty("protective").EnumerateArray().ToArray();
        protective.Select(p => p.GetProperty("scope").GetString()).Should().Equal("auth_failure", "anonymous");
        protective[0].GetProperty("refused").GetInt64().Should().BeGreaterThanOrEqualTo(1);
        protective[1].GetProperty("lastDecisionUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // --- Time series ----------------------------------------------------------------------------

    [Fact]
    public async Task Timeseries_ReturnsExplicitContiguousUtcBuckets()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/usage/timeseries?minutes=30&bucketMinutes=5");

        json.GetProperty("subject").GetString().Should().Be("gateway");
        json.GetProperty("bucketMinutes").GetInt32().Should().Be(5);
        var points = json.GetProperty("points").EnumerateArray().ToArray();
        points.Length.Should().BeInRange(6, 7);
        var starts = points.Select(p => p.GetProperty("startUtc").GetDateTimeOffset()).ToArray();
        starts.Zip(starts.Skip(1)).Should().OnlyContain(pair => pair.Second - pair.First == TimeSpan.FromMinutes(5));
        starts.Should().OnlyContain(s => s.Minute % 5 == 0 && s.Second == 0 && s.Offset == TimeSpan.Zero);
        points[0].TryGetProperty("covered", out _).Should().BeTrue();
        points[0].TryGetProperty("refusedByStreams", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("minutes=0")]
    [InlineData("minutes=181")]
    [InlineData("bucketMinutes=0")]
    [InlineData("bucketMinutes=61")]
    public async Task Timeseries_RefusesAnOutOfRangeQuery_RatherThanAnsweringADifferentOne(string query)
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var response = await client.GetAsync("/admin/api/rate-limits/usage/timeseries?" + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 404 means "nothing recorded for that limit" — which is a limit that does not exist and an
    /// idle one alike, because the counters are created on first use. The console words it that way
    /// rather than drawing a zero line for a string the gateway never saw.
    /// </summary>
    [Fact]
    public async Task Timeseries_WithNothingRecorded_Is404_AndForALimitWithTrafficIsItsOwnSeries()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);
        factory.Services.GetRequiredService<IRateLimitUsageTracker>().RecordRateStage(
            [new RateLimitRule(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(10, 0, 0)) { LimitId = "model:gpt-4" }],
            RateLimitStageOutcome.Refused,
            "m:gpt-4");

        var unknown = await client.GetAsync("/admin/api/rate-limits/usage/timeseries?limitId=model:nope");
        var known = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/usage/timeseries?minutes=3&limitId=model:gpt-4");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        known.GetProperty("limitId").GetString().Should().Be("model:gpt-4");
        known.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("refusedByRate").GetInt64()).Should().Be(1);
    }

    [Fact]
    public async Task ObservabilityRoutes_RequireAnOperator()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/admin/api/rate-limits/usage/timeseries")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/admin/api/rate-limits/history")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- History --------------------------------------------------------------------------------

    [Fact]
    public async Task History_BeforeAnythingIsAudited_SaysUnavailable_NotEmpty()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/history");

        json.GetProperty("available").GetBoolean().Should().BeFalse();
        json.GetProperty("entries").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task History_RecordsAddChangeRemove_ByRuleId_WithVersions()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);

        await SaveAsync(client, Rule("tenant", "acme", 100));
        await SaveAsync(client, Rule("tenant", "acme", 40), Rule("model", "GPT-4", 500));
        await SaveAsync(client, Rule("model", "GPT-4", 500, enabled: false));

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/history");
        var entries = json.GetProperty("entries").EnumerateArray().ToArray();

        json.GetProperty("available").GetBoolean().Should().BeTrue();
        entries.Should().HaveCount(3);
        entries.Should().OnlyContain(e => e.GetProperty("outcome").GetString() == "applied");
        entries.Select(e => e.GetProperty("timestampUtc").GetDateTimeOffset()).Should().BeInDescendingOrder();
        entries[0].GetProperty("version").GetInt64().Should().BeGreaterThan(entries[1].GetProperty("version").GetInt64());
        entries[0].GetProperty("actorApiKeyId").GetString().Should().NotBeNullOrEmpty();

        // The first save replaces the whole set, so it also drops the two seeded protective rules —
        // which is exactly the kind of thing this history exists to show.
        Changes(entries[2]).Should().BeEquivalentTo(
            "added tenant:acme: - => 100rpm+0burst/0streams",
            "removed anonymous:*: 60rpm+20burst/2streams => -",
            "removed auth_failure:*: 60rpm+20burst/0streams => -");
        Changes(entries[1]).Should().BeEquivalentTo(
            "added model:gpt-4: - => 500rpm+0burst/0streams",
            "changed tenant:acme: 100rpm+0burst/0streams => 40rpm+0burst/0streams");
        Changes(entries[0]).Should().BeEquivalentTo(
            "changed model:gpt-4: 500rpm+0burst/0streams => 500rpm+0burst/0streams off",
            "removed tenant:acme: 40rpm+0burst/0streams => -");
    }

    [Fact]
    public async Task History_RecordsARefusedConflict_WithTheVersionItWasBasedOn()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);
        await SaveAsync(client, Rule("tenant", "acme", 100));

        var stale = new HttpRequestMessage(HttpMethod.Put, "/admin/api/rate-limits") { Content = JsonContent.Create(ValidBody()) };
        stale.Headers.TryAddWithoutValidation("If-Match", "W/\"0\"");
        (await client.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var newest = (await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/history"))
            .GetProperty("entries").EnumerateArray().First();

        newest.GetProperty("outcome").GetString().Should().Be("refused");
        newest.GetProperty("statusCode").GetInt32().Should().Be(409);
        newest.GetProperty("basedOnVersion").GetInt64().Should().Be(0);
        newest.GetProperty("message").GetString().Should().Contain("changed by someone else");
        newest.GetProperty("changes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task History_PagesBackwardsWithBefore_AndSkipsOtherAdminActions()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);
        for (var rpm = 10; rpm <= 50; rpm += 10)
        {
            await SaveAsync(client, Rule("tenant", "acme", rpm));
        }

        // Another admin action in the same trail, which a rate-limit history must not show.
        (await client.PostAsync("/admin/api/config/reload", content: null)).EnsureSuccessStatusCode();

        var first = await client.GetFromJsonAsync<JsonElement>("/admin/api/rate-limits/history?take=2");
        var next = first.GetProperty("nextBefore").GetString();
        var second = await client.GetFromJsonAsync<JsonElement>(
            "/admin/api/rate-limits/history?take=10&before=" + Uri.EscapeDataString(next!));

        first.GetProperty("entries").GetArrayLength().Should().Be(2);
        first.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        second.GetProperty("entries").GetArrayLength().Should().Be(3);
        second.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        second.GetProperty("nextBefore").ValueKind.Should().Be(JsonValueKind.Null);
        // The cursor is opaque — it carries a position, not just a timestamp — so the assertion is
        // that the two pages partition the saves, not that the cursor parses as a date.
        var firstIds = first.GetProperty("entries").EnumerateArray().Select(Stamp).ToArray();
        var secondIds = second.GetProperty("entries").EnumerateArray().Select(Stamp).ToArray();
        secondIds.Should().NotIntersectWith(firstIds);
        firstIds.Concat(secondIds).Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }

    /// <summary>The endpoint publishes named fields only, so nothing else in an audit record can leak through it.</summary>
    [Fact]
    public async Task History_PublishesOnlyItsOwnFields_AndNeverTheKey()
    {
        await using var factory = CreateFactory();
        var client = await ClientAsync(factory);
        await SaveAsync(client, Rule("tenant", "acme", 100));

        var body = await client.GetStringAsync("/admin/api/rate-limits/history");
        using var json = JsonDocument.Parse(body);

        body.Should().NotContain(AdminKey);
        json.RootElement.GetProperty("entries")[0].EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(
        [
            "timestampUtc", "outcome", "actorTenantId", "actorApiKeyId", "statusCode", "message", "version",
            "basedOnVersion", "enabled", "adaptiveEnabled", "ruleCount", "changes", "changeCount", "changesTruncated",
        ]);
    }

    // --- Helpers --------------------------------------------------------------------------------

    /// <summary>An audit file of its own: the default path is shared by every host in the bin directory.</summary>
    private static WebApplicationFactory<Program> CreateFactory()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), "33pol-rl-history-" + Guid.NewGuid().ToString("N"), "audit.jsonl");
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings => settings["Gateway:Security:AuditLogPath"] = auditPath);
    }

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory)
    {
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return client;
    }

    private static object ValidBody(params object[] rules) => new
    {
        enabled = true,
        @default = new { rpm = 60, burst = 0, maxConcurrentStreams = 0 },
        plans = new Dictionary<string, object>(),
        rules,
    };

    private static object Rule(string scope, string target, int rpm, bool enabled = true) =>
        new { scope, target, rpm, burst = 0, maxConcurrentStreams = 0, enabled };

    private static async Task SaveAsync(HttpClient client, params object[] rules)
    {
        var response = await client.PutAsJsonAsync("/admin/api/rate-limits", ValidBody(rules));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static string Stamp(JsonElement entry) =>
        entry.GetProperty("timestampUtc").GetDateTimeOffset().ToString("O") + "/" + entry.GetProperty("version").GetInt64();

    private static string[] Changes(JsonElement entry) =>
        [.. entry.GetProperty("changes").EnumerateArray().Select(c =>
            $"{c.GetProperty("kind").GetString()} {c.GetProperty("ruleId").GetString()}: "
            + $"{c.GetProperty("before").GetString() ?? "-"} => {c.GetProperty("after").GetString() ?? "-"}")];
}
