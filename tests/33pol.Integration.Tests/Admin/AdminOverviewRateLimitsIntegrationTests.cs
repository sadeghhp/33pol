using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pol33.App.DependencyInjection.Overview;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Integration.Tests.Support;
using Pol33.Observability.RateLimiting;
using Pol33.Persistence;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// <c>GET /admin/api/overview/rate-limits</c>: the Overview's rate-limit section, driven through the
/// real usage tracker, and the Attention items it feeds into the summary.
/// </summary>
public sealed class AdminOverviewRateLimitsIntegrationTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";
    private const string Path = "/admin/api/overview/rate-limits?refresh=true";

    [Fact]
    public async Task WithoutAdminKey_IsUnauthorized()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        (await factory.CreateClient().GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A composition without the usage tracker. The tracker cannot simply be removed from the
    /// container — the rate-limit usage endpoints bind it at startup — so the section service is
    /// built the way DI builds it, from the same container, minus the tracker.
    /// </summary>
    [Fact]
    public async Task TrackerUnavailable_Answers204()
    {
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GatewayOverviewSectionService>();
                services.AddSingleton(sp => new GatewayOverviewSectionService(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IOptions<GatewayOptions>>(),
                    sp.GetRequiredService<IOptions<BillingOptions>>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GatewayOverviewSectionService>>(),
                    sp.GetRequiredService<IModelRegistry>(),
                    configProvider: sp.GetService<IGatewayConfigProvider>(),
                    configReload: sp.GetService<IConfigReload>(),
                    rateLimitTracker: null,
                    rateLimitAdmin: sp.GetService<IRateLimitConfigAdminService>()));
            }));
        var client = await ClientAsync(factory);

        (await client.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        AttentionCodes(summary).Should().NotContain(c => c.StartsWith("rate_limit_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZeroTraffic_IsAValidSectionWithRealZeros()
    {
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions()));
        await using var factory = CreateFactory(tracker);
        var client = await ClientAsync(factory);

        var response = await client.GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "zero traffic is data, not a missing section");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("enforced").GetBoolean().Should().BeTrue();
        json.GetProperty("lastHour").GetProperty("minutes").GetInt32().Should().Be(60);
        json.GetProperty("lastHour").GetProperty("decisions").GetInt64().Should().Be(0);
        json.GetProperty("lastHour").GetProperty("refused").GetInt64().Should().Be(0);
        json.GetProperty("lastHour").GetProperty("refusalShare").GetDouble().Should().Be(0);
        json.GetProperty("lastFiveMinutes").GetProperty("minutes").GetInt32().Should().Be(5);
        json.GetProperty("refusingLimitCount").GetInt32().Should().Be(0);
        json.GetProperty("limits").GetArrayLength().Should().Be(0);
        json.GetProperty("topRefusedTenants").GetArrayLength().Should().Be(0);
        json.GetProperty("protective").EnumerateArray().Select(p => p.GetProperty("scope").GetString())
            .Should().Equal("auth_failure", "anonymous");
        json.GetProperty("tracker").GetProperty("isSaturated").GetBoolean().Should().BeFalse();
        json.GetProperty("tracker").GetProperty("atCapacity").GetArrayLength().Should().Be(0);

        var retention = json.GetProperty("retention");
        retention.GetProperty("historyMinutes").GetInt32().Should().Be(RateLimitUsageTracker.WindowMinutes);
        retention.GetProperty("processLocal").GetBoolean().Should().BeTrue();
        retention.GetProperty("trackingSinceUtc").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        var schedule = json.GetProperty("schedule");
        schedule.GetProperty("available").GetBoolean().Should().BeTrue();
        schedule.GetProperty("windowsActiveNow").GetInt32().Should().Be(0);
        schedule.GetProperty("nextChangeAtUtc").ValueKind.Should().Be(JsonValueKind.Null, "nothing is scheduled");
    }

    [Fact]
    public async Task EnforcedWithRules_AndNoRefusals_IsAQuietSection()
    {
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions()));
        await using var factory = CreateFactory(tracker);
        var client = await ClientAsync(factory);
        (await client.PutAsJsonAsync("/admin/api/rate-limits", Body(Rule("model", "local-mock", 500)))).EnsureSuccessStatusCode();
        for (var i = 0; i < 25; i++)
        {
            tracker.Record(new RateLimitUsageEvent("t-1", "k-1", "local-mock", true, RateLimitScope.Model, RateLimitControl.Rate, 500, 500));
        }

        var json = await client.GetFromJsonAsync<JsonElement>(Path);

        json.GetProperty("enforced").GetBoolean().Should().BeTrue();
        json.GetProperty("ruleCount").GetInt32().Should().Be(1);
        json.GetProperty("lastHour").GetProperty("decisions").GetInt64().Should().Be(25);
        json.GetProperty("lastHour").GetProperty("refused").GetInt64().Should().Be(0);
        json.GetProperty("refusedTenantCount").GetInt32().Should().Be(0);
        json.GetProperty("refusingLimitCount").GetInt32().Should().Be(0);
        json.GetProperty("limits").GetArrayLength().Should().Be(0);

        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        AttentionCodes(summary).Should().NotContain(c => c.StartsWith("rate_limit_", StringComparison.Ordinal));
    }

    /// <summary>
    /// One gateway with a bit of everything: refusals thirty minutes ago and now, a scoped rule and a
    /// plan tier refusing, an anonymous caller, a protective refusal, a governor holding two models
    /// down, a nearly full partition table and a schedule with one window running.
    /// </summary>
    [Fact]
    public async Task Section_AttributesWindowsLimitsSubjectsAndState()
    {
        var time = new ShiftableTime();
        var governor = new FixedGovernor(new AdaptiveRateLimitSnapshot(
            true,
            [
                new AdaptiveModelState("local-mock", 0.8, 0.9, "saturated", DateTimeOffset.UtcNow),
                new AdaptiveModelState("gpt-4o", 0.5, 0.95, "saturated", DateTimeOffset.UtcNow),
                new AdaptiveModelState("idle", 1.0, 0.1, "steady", DateTimeOffset.UtcNow),
            ],
            BackedOffPartitions: 3,
            LastEvaluatedUtc: DateTimeOffset.UtcNow));
        var store = new FixedStore(new RateLimitStoreStats(45_000, 1_000, 50_000));
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions()), governor, store, time);
        await using var factory = CreateFactory(tracker, settings =>
        {
            settings["Gateway:Overview:Attention:RateLimitRefusalForSeconds"] = "0";
            settings["Gateway:Overview:Attention:RateLimitPartitionsNearCeilingForSeconds"] = "0";
        });
        var client = await ClientAsync(factory);

        var (tenantId, tenantSlug, keyId, keyLabel) = await BootstrapIdentityAsync(factory);
        var now = DateTimeOffset.UtcNow;
        (await client.PutAsJsonAsync("/admin/api/rate-limits", Body(
            Rule("model", "local-mock", 500, new { name = "launch", kind = "once", rpm = 50, burst = 0, maxConcurrentStreams = 0, from = now.AddHours(-1), until = now.AddHours(1) }),
            Rule("global", "*", 100_000, new { name = "later", kind = "once", rpm = 10, burst = 0, maxConcurrentStreams = 0, from = now.AddHours(2), until = now.AddHours(3) }))))
            .EnsureSuccessStatusCode();

        // Thirty minutes ago: in the hour, not in the last five minutes.
        time.Offset = TimeSpan.FromMinutes(-30);
        Decisions(tracker, tenantId.ToString(), keyId.ToString(), admitted: 6, refused: 4);

        // Now: 20 decisions, 3 refused — 15% of the last five minutes.
        time.Offset = TimeSpan.Zero;
        Decisions(tracker, tenantId.ToString(), keyId.ToString(), admitted: 16, refused: 2);
        Decisions(tracker, RateLimitPartitionPrefixAnonymous + "203.0.113.0/24", null, admitted: 1, refused: 1);

        var modelRule = new RateLimitRule(RateLimitScope.Model, "m:local-mock", new RateLimitPolicy(10, 0, 0)) { LimitId = "model:local-mock" };
        var planTier = new RateLimitRule(RateLimitScope.Tenant, "t:" + tenantId, new RateLimitPolicy(100, 0, 0)) { LimitId = "plan:pro" };
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordRateStage([modelRule], RateLimitStageOutcome.Charged);
            tracker.RecordRateStage([planTier], RateLimitStageOutcome.Charged);
        }

        tracker.RecordRateStage([modelRule], RateLimitStageOutcome.Refused, modelRule.PartitionKey);
        tracker.RecordRateStage([modelRule], RateLimitStageOutcome.Refused, modelRule.PartitionKey);
        tracker.RecordRateStage([planTier], RateLimitStageOutcome.Refused, planTier.PartitionKey);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Refused, 30);

        // A key-scoped rule on a known key, one on a key that no longer exists, and a rule that ran at
        // its rate without refusing anything.
        var keyRule = new RateLimitRule(RateLimitScope.ApiKey, "k:" + keyId, new RateLimitPolicy(10, 0, 0)) { LimitId = "api_key:" + keyId };
        var goneKey = Guid.NewGuid();
        var goneRule = new RateLimitRule(RateLimitScope.ApiKeyModel, "km:" + goneKey, new RateLimitPolicy(10, 0, 0)) { LimitId = "api_key_model:" + goneKey + "|local-mock" };
        var busyRule = new RateLimitRule(RateLimitScope.Model, "m:busy", new RateLimitPolicy(10, 0, 0)) { LimitId = "model:busy" };
        tracker.RecordRateStage([keyRule], RateLimitStageOutcome.Refused, keyRule.PartitionKey);
        tracker.RecordRateStage([goneRule], RateLimitStageOutcome.Refused, goneRule.PartitionKey);
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordRateStage([busyRule], RateLimitStageOutcome.Charged);
        }

        var json = await client.GetFromJsonAsync<JsonElement>(Path);

        // Windows
        var hour = json.GetProperty("lastHour");
        hour.GetProperty("decisions").GetInt64().Should().Be(30);
        hour.GetProperty("refused").GetInt64().Should().Be(7);
        var five = json.GetProperty("lastFiveMinutes");
        five.GetProperty("decisions").GetInt64().Should().Be(20);
        five.GetProperty("refused").GetInt64().Should().Be(3);
        five.GetProperty("refusalShare").GetDouble().Should().BeApproximately(0.15, 1e-9);

        // Limits: stable rule identity, and a peak only where one bucket was drained
        json.GetProperty("refusingLimitCount").GetInt32().Should().Be(4, "the near-limit rule refused nothing and is not counted");
        var limits = json.GetProperty("limits").EnumerateArray().ToArray();
        limits.Select(l => l.GetProperty("limitId").GetString()).Should().BeEquivalentTo(
            "model:local-mock", "plan:pro", "api_key:" + keyId.ToString().ToLowerInvariant(), "api_key_model:" + goneKey.ToString().ToLowerInvariant() + "|local-mock", "model:busy");
        limits[0].GetProperty("limitId").GetString().Should().Be("model:local-mock", "most refused first");
        limits[^1].GetProperty("limitId").GetString().Should().Be("model:busy", "near-limit rows follow every refusing row");
        var rule = limits[0];
        rule.GetProperty("ruleId").GetString().Should().Be("model:local-mock");
        rule.GetProperty("refused").GetInt64().Should().Be(2);
        rule.GetProperty("singleBucket").GetBoolean().Should().BeTrue();
        rule.GetProperty("peakUtilization").GetDouble().Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(0.5);
        var plan = limits.Single(l => l.GetProperty("limitId").GetString() == "plan:pro");
        plan.GetProperty("ruleId").ValueKind.Should().Be(JsonValueKind.Null, "a plan tier is not a rule in the list");
        plan.GetProperty("singleBucket").GetBoolean().Should().BeFalse();
        plan.GetProperty("peakUtilization").ValueKind.Should().Be(JsonValueKind.Null, "a tier sums many buckets");
        plan.GetProperty("nearLimit").GetBoolean().Should().BeFalse();
        rule.GetProperty("nearLimit").GetBoolean().Should().BeFalse("it refused");
        var near = limits.Single(l => l.GetProperty("limitId").GetString() == "model:busy");
        near.GetProperty("refused").GetInt64().Should().Be(0);
        near.GetProperty("nearLimit").GetBoolean().Should().BeTrue();
        near.GetProperty("peakUtilization").GetDouble().Should().BeGreaterThanOrEqualTo(0.8);

        // Key-scoped limits are named by label or prefix, never by key id; navigation keeps the id.
        var keyLimit = limits.Single(l => l.GetProperty("scope").GetString() == "api_key");
        keyLimit.GetProperty("targetLabel").GetString().Should().Be(keyLabel);
        keyLimit.GetProperty("ruleId").GetString().Should().Be("api_key:" + keyId.ToString().ToLowerInvariant());
        var goneLimit = limits.Single(l => l.GetProperty("scope").GetString() == "api_key_model");
        goneLimit.GetProperty("targetLabel").GetString().Should().Be("unknown key · local-mock");
        rule.GetProperty("targetLabel").ValueKind.Should().Be(JsonValueKind.Null, "a model target is already readable");

        // Subjects, with labels
        json.GetProperty("refusedTenantCount").GetInt32().Should().Be(2);
        json.GetProperty("refusedKeyCount").GetInt32().Should().Be(1);
        var tenants = json.GetProperty("topRefusedTenants").EnumerateArray().ToArray();
        tenants[0].GetProperty("key").GetString().Should().Be(tenantId.ToString());
        tenants[0].GetProperty("label").GetString().Should().Be(tenantSlug);
        tenants[0].GetProperty("refused").GetInt64().Should().Be(6);
        tenants[1].GetProperty("anonymous").GetBoolean().Should().BeTrue();
        tenants[1].GetProperty("label").GetString().Should().Be("anonymous");
        tenants[1].GetProperty("key").GetString().Should().Be("anonymous:1");
        json.GetRawText().Should().NotContain("203.0.113", "an anonymous caller's address block is not part of this section");
        var key = json.GetProperty("topRefusedKeys").EnumerateArray().Single();
        key.GetProperty("key").GetString().Should().Be(keyId.ToString());
        key.GetProperty("label").GetString().Should().Be(keyLabel);
        key.GetProperty("tenantSlug").GetString().Should().Be(tenantSlug);
        key.GetRawText().Should().NotContain("keyHash").And.NotContain("KeyHash");

        // Protective, adaptive, store
        var authFailure = json.GetProperty("protective")[0];
        authFailure.GetProperty("scope").GetString().Should().Be("auth_failure");
        authFailure.GetProperty("refused").GetInt64().Should().Be(1);
        // Last writer wins: the admin client's own requests pass through the same check.
        authFailure.GetProperty("enforcedRpm").GetInt32().Should().BeGreaterThan(0);
        authFailure.GetProperty("checked").GetInt64().Should().BeGreaterThan(0);
        var adaptive = json.GetProperty("adaptive");
        adaptive.GetProperty("enabled").GetBoolean().Should().BeTrue();
        adaptive.GetProperty("modelsReduced").GetInt32().Should().Be(2);
        adaptive.GetProperty("lowestFactor").GetDouble().Should().Be(0.5);
        adaptive.GetProperty("lowestFactorModelId").GetString().Should().Be("gpt-4o");
        adaptive.GetProperty("shedding").GetBoolean().Should().BeTrue();
        adaptive.GetProperty("backedOffPartitions").GetInt32().Should().Be(3);
        var storeJson = json.GetProperty("store");
        storeJson.GetProperty("requestPartitions").GetInt32().Should().Be(45_000);
        storeJson.GetProperty("maxPartitions").GetInt32().Should().Be(50_000);
        storeJson.GetProperty("ratio").GetDouble().Should().BeApproximately(0.9, 1e-9);

        // Schedule, from the schedule report: one window running, the soonest change is its end
        json.GetProperty("ruleCount").GetInt32().Should().Be(2);
        var schedule = json.GetProperty("schedule");
        schedule.GetProperty("available").GetBoolean().Should().BeTrue();
        schedule.GetProperty("scheduledRuleCount").GetInt32().Should().Be(2);
        schedule.GetProperty("windowsActiveNow").GetInt32().Should().Be(1);
        schedule.GetProperty("nextChangeAtUtc").GetDateTimeOffset().Should().BeCloseTo(now.AddHours(1), TimeSpan.FromSeconds(1));
        schedule.GetProperty("nextChangeRuleId").GetString().Should().Be("model:local-mock");

        // Attention, through the summary
        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        var items = summary.GetProperty("attention").EnumerateArray().ToArray();
        var refusing = items.Single(i => i.GetProperty("code").GetString() == "rate_limit_refusing");
        refusing.GetProperty("severity").GetString().Should().Be("warning");
        refusing.GetProperty("link").GetProperty("tab").GetString().Should().Be("settings");
        refusing.GetProperty("link").GetProperty("params").GetProperty("sub").GetString().Should().Be("limits");
        AttentionCodes(summary).Should().Contain("rate_limit_partitions_near_ceiling")
            .And.Contain("rate_limit_adaptive_shedding")
            .And.NotContain("rate_limit_not_enforced")
            .And.NotContain("rate_limit_tracker_saturated");
    }

    [Fact]
    public async Task SaturatedTracker_IsReportedAndRaisesAttention()
    {
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions { UsageReportMaxKeys = 10 }));
        await using var factory = CreateFactory(tracker);
        var client = await ClientAsync(factory);
        for (var i = 0; i < 12; i++)
        {
            tracker.Record(new RateLimitUsageEvent("tenant-" + i, null, "local-mock", true, RateLimitScope.Tenant, RateLimitControl.Rate, 60, 60));
        }

        var json = await client.GetFromJsonAsync<JsonElement>(Path);

        var t = json.GetProperty("tracker");
        t.GetProperty("isSaturated").GetBoolean().Should().BeTrue();
        t.GetProperty("droppedDecisions").GetInt64().Should().BeGreaterThan(0);
        t.GetProperty("maxKeysPerDimension").GetInt32().Should().Be(10);
        t.GetProperty("atCapacity").EnumerateArray().Select(e => e.GetString()).Should().Contain("tenants");
        json.GetProperty("lastHour").GetProperty("decisions").GetInt64().Should().Be(12, "totals are unbounded and stay exact");

        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        AttentionCodes(summary).Should().Contain("rate_limit_tracker_saturated");
    }

    [Fact]
    public async Task EnforcementSwitchedOff_WithRules_RaisesNotEnforced()
    {
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions()));
        await using var factory = CreateFactory(tracker, settings =>
            settings["Gateway:Overview:Attention:RateLimitNotEnforcedForSeconds"] = "0");
        var client = await ClientAsync(factory);
        (await client.PutAsJsonAsync("/admin/api/rate-limits", Body(enabled: false, Rule("model", "local-mock", 500)))).EnsureSuccessStatusCode();

        var json = await client.GetFromJsonAsync<JsonElement>(Path);
        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");

        json.GetProperty("enforced").GetBoolean().Should().BeFalse();
        json.GetProperty("configReloadInProgress").GetBoolean().Should().BeFalse();
        AttentionCodes(summary).Should().Contain("rate_limit_not_enforced");
    }

    [Fact]
    public async Task Section_IsMemoisedUntilRefreshIsRequested()
    {
        var tracker = new RateLimitUsageTracker(Options.Create(new RateLimitingOptions()));
        await using var factory = CreateFactory(tracker, settings => settings["Gateway:Overview:SlowSectionTtlSeconds"] = "600");
        var client = await ClientAsync(factory);
        var first = await client.GetFromJsonAsync<JsonElement>(Path);
        tracker.Record(new RateLimitUsageEvent("t", null, "local-mock", false, RateLimitScope.Tenant, RateLimitControl.Rate, 1, 1));

        var memo = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/rate-limits");
        var rebuilt = await client.GetFromJsonAsync<JsonElement>(Path);

        memo.GetProperty("builtAtUtc").GetDateTimeOffset().Should().Be(first.GetProperty("builtAtUtc").GetDateTimeOffset());
        memo.GetProperty("lastHour").GetProperty("refused").GetInt64().Should().Be(0);
        rebuilt.GetProperty("lastHour").GetProperty("refused").GetInt64().Should().Be(1);
    }

    // ---- helpers ----

    /// <summary>The anonymous partition prefix, as the proxy writes it (<c>RateLimitPartition.AnonymousPrefix</c>).</summary>
    private const string RateLimitPartitionPrefixAnonymous = "anon:";

    private static void Decisions(RateLimitUsageTracker tracker, string tenant, string? key, int admitted, int refused)
    {
        for (var i = 0; i < admitted; i++)
        {
            tracker.Record(new RateLimitUsageEvent(tenant, key, "local-mock", true, RateLimitScope.Tenant, RateLimitControl.Rate, 60, 60));
        }

        for (var i = 0; i < refused; i++)
        {
            tracker.Record(new RateLimitUsageEvent(tenant, key, "local-mock", false, RateLimitScope.Model, RateLimitControl.Rate, 10, 10));
        }
    }

    private static IEnumerable<string> AttentionCodes(JsonElement summary) =>
        summary.TryGetProperty("attention", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray()
            : [];

    private static object Rule(string scope, string target, int rpm, params object[] schedule) => new
    {
        scope,
        target,
        rpm,
        burst = 0,
        maxConcurrentStreams = 0,
        schedule,
    };

    private static object Body(params object[] rules) => Body(true, rules);

    private static object Body(bool enabled, params object[] rules) => new
    {
        enabled,
        @default = new { rpm = 10_000, burst = 0, maxConcurrentStreams = 100 },
        plans = new Dictionary<string, object>(),
        rules,
    };

    private static async Task<(Guid TenantId, string TenantSlug, Guid KeyId, string KeyLabel)> BootstrapIdentityAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        var key = await db.ApiKeys.AsNoTracking().FirstAsync(k => k.TenantId == tenant.Id);
        return (tenant.Id, tenant.Slug, key.Id, string.IsNullOrWhiteSpace(key.Label) ? key.KeyPrefix : key.Label!);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IRateLimitUsageTracker? tracker = null,
        Action<IDictionary<string, string?>>? configure = null)
    {
        var auditPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "33pol-rl-overview-" + Guid.NewGuid().ToString("N"), "audit.jsonl");
        var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings =>
            {
                settings["Gateway:Security:AuditLogPath"] = auditPath;
                configure?.Invoke(settings);
            });
        return tracker is null
            ? factory
            : factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRateLimitUsageTracker>();
                services.AddSingleton(tracker);
            }));
    }

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory)
    {
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return client;
    }

    private sealed class ShiftableTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    private sealed class FixedGovernor(AdaptiveRateLimitSnapshot snapshot) : IAdaptiveRateLimitGovernor
    {
        public bool IsEnabled => snapshot.Enabled;

        public double GetModelFactor(string modelId) => 1.0;

        public int GetRetryAfterSeconds(string partitionKey, int baseRetryAfterSeconds, DateTimeOffset now) => baseRetryAfterSeconds;

        public void RecordOutcome(string partitionKey, bool admitted, DateTimeOffset now)
        {
        }

        public void Evaluate(DateTimeOffset now)
        {
        }

        public AdaptiveRateLimitSnapshot Snapshot() => snapshot;
    }

    /// <summary>Only <see cref="GetStats"/> is read by the tracker; this instance never enforces anything.</summary>
    private sealed class FixedStore(RateLimitStoreStats stats) : IDistributedRateLimitStore
    {
        public RateLimitStoreStats GetStats() => stats;

        public RateLimitAcquireResult TryAcquireRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now) => throw new NotSupportedException();

        public RateLimitAcquireResult TryAcquireAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now) => throw new NotSupportedException();

        public void RefundAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now) => throw new NotSupportedException();

        public RateLimitAcquireResult PeekRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now) => throw new NotSupportedException();

        public void DebitRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now) => throw new NotSupportedException();

        public void RefundRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now) => throw new NotSupportedException();

        public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy) => throw new NotSupportedException();

        public RateLimitAcquireResult TryAcquireStreamSlots(ReadOnlySpan<RateLimitRule> rules, out RateLimitSlotLease held) => throw new NotSupportedException();

        public void ReleaseStreamSlot(string partitionKey) => throw new NotSupportedException();

        public void ReleaseStreamSlots(RateLimitSlotLease held) => throw new NotSupportedException();

        public int Compact(DateTimeOffset now) => throw new NotSupportedException();
    }
}
