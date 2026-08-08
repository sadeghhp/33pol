using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Integration.Tests.Support;
using Pol33.Persistence;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUsageIntegrationTests
{
    [Fact]
    public async Task GetUsage_WithoutAdminKey_ReturnsUnauthorized()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        var response = await client.GetAsync("/admin/api/usage");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUsage_WithSeededRollups_ReturnsSummaryTotals()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await response.Content.ReadFromJsonAsync<UsageReportDto>();
        report.Should().NotBeNull();
        report!.Rollups.Should().HaveCount(1);
        report.Summary!.TotalPromptTokens.Should().Be(100);
        report.Summary.TotalCompletionTokens.Should().Be(50);
        report.Summary.TotalCost.Should().Be(0.15m);
        report.Summary.TotalRequests.Should().Be(2);
    }

    [Fact]
    public async Task ExportUsage_Csv_ReturnsSeededRow()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/export?format=csv");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("usage_date,tenant_id");
        body.Should().Contain("gpt-4o");
        body.Should().Contain("eng");
        body.Should().Contain("100");
        body.Should().Contain("0.15");
    }

    [Fact]
    public async Task GetForecast_WithoutAdminKey_ReturnsUnauthorized()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        var response = await client.GetAsync("/admin/api/usage/forecast?days=7");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetEvents_WithSeededBillingEvent_ReturnsRow()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedBillingEventAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/events?limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BillingEventsPageDto>();
        page.Should().NotBeNull();
        page!.Events.Should().HaveCount(1);
        page.Events![0].ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task GetForecast_WithSeededRollups_ReturnsProjection()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/forecast?days=7");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var forecast = await response.Content.ReadFromJsonAsync<UsageForecastDto>();
        forecast.Should().NotBeNull();
        forecast!.TrailingTotalCost.Should().Be(0.15m);
        forecast.ProjectedMonthlyCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task ExportUsage_Json_ReturnsSeededRollups()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/export?format=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"modelId\": \"gpt-4o\"");
        body.Should().Contain("\"costCenter\": \"eng\"");
    }

    private static async Task<Guid> GetBootstrapTenantIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        return tenant.Id;
    }

    private static async Task SeedBillingEventAsync(WebApplicationFactory<Program> factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IBillingEventRepository>();
        await events.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(),
            "req-seed-events",
            tenantId,
            null,
            "gpt-4o",
            "eng",
            10,
            5,
            null,
            null,
            0.01m,
            100,
            DateTimeOffset.UtcNow));
    }

    private static async Task SeedRollupsAsync(
        WebApplicationFactory<Program> factory,
        Guid tenantId,
        DateOnly? usageDate = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rollups = scope.ServiceProvider.GetRequiredService<IDailyUsageRollupRepository>();
        await rollups.UpsertRollupsAsync([
            new DailyUsageRollupRecord(
                usageDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                tenantId,
                "gpt-4o",
                "eng",
                100,
                50,
                0.15m,
                2),
        ]);
    }

    private sealed class UsageReportDto
    {
        public UsageSummaryDto? Summary { get; init; }

        public List<object>? Rollups { get; init; }
    }

    private sealed class UsageSummaryDto
    {
        public long TotalPromptTokens { get; init; }

        public long TotalCompletionTokens { get; init; }

        public decimal TotalCost { get; init; }

        public int TotalRequests { get; init; }
    }

    private sealed class UsageForecastDto
    {
        public int TrailingDays { get; init; }

        public decimal TrailingTotalCost { get; init; }

        public decimal ProjectedMonthlyCost { get; init; }
    }

    private sealed class BillingEventsPageDto
    {
        public List<BillingEventDto>? Events { get; init; }

        public int Limit { get; init; }
    }

    private sealed class BillingEventDto
    {
        public string? ModelId { get; init; }
    }
}
