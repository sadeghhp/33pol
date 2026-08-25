using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Integration.Tests.Support;
using Pol33.Persistence;
using Pol33.Persistence.Entities;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The Overview's slow, database-backed sections under <c>/admin/api/overview/*</c>.
/// </summary>
public sealed class AdminOverviewSectionsIntegrationTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Theory]
    [InlineData("/admin/api/overview/finops")]
    [InlineData("/admin/api/overview/policy")]
    [InlineData("/admin/api/overview/control-plane")]
    [InlineData("/admin/api/overview/activity")]
    [InlineData("/admin/api/overview/tenants")]
    public async Task OverviewSections_WithoutAdminKey_AreUnauthorized(string path)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FinOps_WithSeededSpendAndBudget_ReportsTodayMonthToDateCoverageAndBudgetRatio()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedRollupAsync(factory, tenantId, today, "gpt-4o", 0.15m, "eng");
        await SeedRollupAsync(factory, tenantId, today, "local-mock", 0.05m, null);
        await SeedBudgetAsync(factory, tenantId, "R&D", limit: 1m, hardStop: true);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var response = await client.GetAsync("/admin/api/overview/finops");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        json.GetProperty("todayCost").GetDecimal().Should().Be(0.20m);
        json.GetProperty("monthToDateCost").GetDecimal().Should().Be(0.20m);
        json.GetProperty("todayRequests").GetInt64().Should().Be(4);
        json.GetProperty("currency").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("registeredModelCount").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("pricedModelCount").GetInt32().Should().Be(0, "no rate cards are seeded");
        json.GetProperty("unpricedModelIds").EnumerateArray().Select(e => e.GetString()).Should().Contain("local-mock");

        var topModels = json.GetProperty("topModelsMonthToDate").EnumerateArray().ToList();
        topModels[0].GetProperty("key").GetString().Should().Be("gpt-4o");
        var costCenters = json.GetProperty("topCostCentersMonthToDate").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        costCenters.Should().Contain("eng").And.Contain("(none)");

        var budget = json.GetProperty("budgets").EnumerateArray().Single();
        budget.GetProperty("name").GetString().Should().Be("R&D");
        budget.GetProperty("spent").GetDecimal().Should().Be(0.20m);
        budget.GetProperty("limit").GetDecimal().Should().Be(1m);
        budget.GetProperty("ratio").GetDouble().Should().BeApproximately(0.2, 1e-9);
        budget.GetProperty("hardStopEnabled").GetBoolean().Should().BeTrue();
        budget.GetProperty("tenantSlug").GetString().Should().NotBeNullOrEmpty();

        json.GetProperty("reconciliation").GetProperty("enabled").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task FinOps_IsMemoisedUntilRefreshIsRequested()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var first = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/finops");
        first.GetProperty("todayCost").GetDecimal().Should().Be(0m);

        await SeedRollupAsync(factory, tenantId, today, "gpt-4o", 0.15m, "eng");

        var cached = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/finops");
        cached.GetProperty("todayCost").GetDecimal().Should().Be(0m, "the section is served from memory inside its TTL");

        var refreshed = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/finops?refresh=true");
        refreshed.GetProperty("todayCost").GetDecimal().Should().Be(0.15m);
    }

    [Fact]
    public async Task Policy_ListsQuotaConsumptionAndUnknownModels()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var tenantId = await GetBootstrapTenantIdAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IQuotaService>().CommitUsage(tenantId.ToString(), "gpt-4o", 1234, "req-policy-1");
            scope.ServiceProvider.GetRequiredService<IGatewayMetricsCollector>().RecordModelResolve("not_found", "gpt-99");
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/policy?refresh=true");
        var quota = json.GetProperty("quotas").EnumerateArray().Single(q => q.GetProperty("partitionKey").GetString() == tenantId.ToString());
        quota.GetProperty("used").GetInt64().Should().Be(1234);
        quota.GetProperty("limit").GetInt64().Should().BeGreaterThan(0);
        quota.GetProperty("tenantSlug").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("unknownModels").EnumerateArray().Should().Contain(r => r.GetProperty("key").GetString() == "gpt-99");

        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        summary.GetProperty("policy").GetProperty("unknownModels1h").EnumerateArray()
            .Should().Contain(r => r.GetProperty("key").GetString() == "gpt-99");
        summary.GetProperty("policy").GetProperty("rejectionsByReason1h").EnumerateArray()
            .Should().Contain(r => r.GetProperty("key").GetString() == "model_not_found");
    }

    [Fact]
    public async Task ControlPlane_AfterABackupAttempt_ReportsItAndTheConfigReloadStamp()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        // The in-memory provider has no file to back up, so the attempt is recorded as failed.
        (await client.PostAsync("/admin/api/maintenance/backup", content: null)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync("/admin/api/config/reload", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/control-plane?refresh=true");

        json.GetProperty("database").GetProperty("configured").GetBoolean().Should().BeFalse("in-memory is not a SQLite file");
        var backup = json.GetProperty("lastBackup");
        backup.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        backup.GetProperty("error").GetString().Should().Contain("No relational database");
        json.GetProperty("configLastReloadUtc").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        json.GetProperty("secrets").GetProperty("hasRun").GetBoolean().Should().BeTrue();
        json.GetProperty("modelCount").GetInt32().Should().BeGreaterThan(0);

        var status = await client.GetFromJsonAsync<JsonElement>("/admin/api/config/status");
        status.GetProperty("lastReload").ValueKind.Should().Be(JsonValueKind.String);

        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        summary.GetProperty("controlPlane").GetProperty("workingSetBytes").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Activity_ListsAuditedAdminActionsNewestFirst()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);
        var modelId = "activity-" + Guid.NewGuid().ToString("N")[..8];

        var create = await client.PostAsJsonAsync("/admin/api/models", new { model = new { id = modelId, url = "http://127.0.0.1:18080" } });
        create.IsSuccessStatusCode.Should().BeTrue();

        var response = await client.GetAsync("/admin/api/overview/activity?limit=50&refresh=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        json.GetProperty("available").GetBoolean().Should().BeTrue();
        var entries = json.GetProperty("entries").EnumerateArray().ToList();
        var created = entries.First(e => e.GetProperty("action").GetString() == "model.create" && (e.GetProperty("details").GetString() ?? "").Contains(modelId));
        created.GetProperty("tenantSlug").GetString().Should().NotBeNullOrEmpty();
        created.GetProperty("apiKeyLabel").GetString().Should().NotBeNullOrEmpty();
        entries.Select(e => e.GetProperty("timestampUtc").GetDateTimeOffset()).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Tenants_ListsConsumersExpiringAndIdleKeys()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedRollupAsync(factory, tenantId, today, "gpt-4o", 0.15m, "eng");
        await SeedRollupAsync(factory, null, today, "local-mock", 0m, null);
        await SeedKeyAsync(factory, tenantId, "sk-33pol-expi", "ci-deploy", expiresAt: DateTimeOffset.UtcNow.AddDays(2), lastUsedAt: DateTimeOffset.UtcNow);
        await SeedKeyAsync(factory, tenantId, "sk-33pol-idle", "old-laptop", expiresAt: null, lastUsedAt: DateTimeOffset.UtcNow.AddDays(-90));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/overview/tenants?refresh=true");

        json.GetProperty("tenantCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        json.GetProperty("keyCount").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        json.GetProperty("expiringKeys").EnumerateArray().Select(k => k.GetProperty("label").GetString()).Should().Contain("ci-deploy").And.NotContain("old-laptop");
        json.GetProperty("idleKeys").EnumerateArray().Select(k => k.GetProperty("label").GetString()).Should().Contain("old-laptop").And.NotContain("ci-deploy");
        var consumers = json.GetProperty("topConsumersMonthToDate").EnumerateArray().ToList();
        consumers[0].GetProperty("tenantSlug").GetString().Should().NotBeNullOrEmpty();
        consumers[0].GetProperty("cost").GetDecimal().Should().Be(0.15m);
        consumers.Should().Contain(c => c.GetProperty("tenantSlug").GetString() == "anonymous");
        json.GetProperty("anonymousRequestShare").GetDouble().Should().BeApproximately(0.5, 1e-9);

        var summary = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
        summary.GetProperty("attention").EnumerateArray().Select(a => a.GetProperty("code").GetString())
            .Should().Contain("key_expiring").And.Contain("key_idle");
    }

    private static async Task SeedKeyAsync(WebApplicationFactory<Program> factory, Guid tenantId, string prefix, string label, DateTimeOffset? expiresAt, DateTimeOffset? lastUsedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = prefix,
            Role = Pol33.Core.Identity.ApiKeyRole.Inference,
            Scopes = [],
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-120),
            LastUsedAt = lastUsedAt,
            Label = label,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> GetBootstrapTenantIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        return tenant.Id;
    }

    private static async Task SeedRollupAsync(WebApplicationFactory<Program> factory, Guid? tenantId, DateOnly date, string modelId, decimal cost, string? costCenter)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rollups = scope.ServiceProvider.GetRequiredService<IDailyUsageRollupRepository>();
        await rollups.UpsertRollupsAsync([new DailyUsageRollupRecord(date, tenantId, modelId, costCenter, 100, 50, cost, 2)]);
    }

    private static async Task SeedBudgetAsync(WebApplicationFactory<Program> factory, Guid tenantId, string name, decimal limit, bool hardStop)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.Budgets.Add(new BudgetEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            AmountLimit = limit,
            Currency = "USD",
            WarningThresholdRatio = 0.8m,
            HardStopEnabled = hardStop,
            PeriodStartDay = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
