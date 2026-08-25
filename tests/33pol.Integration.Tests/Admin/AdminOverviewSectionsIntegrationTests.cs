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

    private static async Task<Guid> GetBootstrapTenantIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        return tenant.Id;
    }

    private static async Task SeedRollupAsync(WebApplicationFactory<Program> factory, Guid tenantId, DateOnly date, string modelId, decimal cost, string? costCenter)
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
