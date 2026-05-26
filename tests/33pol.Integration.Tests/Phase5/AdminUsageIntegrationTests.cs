using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Integration.Tests.Support;

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

        var tenantId = Guid.NewGuid();
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync($"/admin/api/usage?tenantId={tenantId}");
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

        var tenantId = Guid.NewGuid();
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync($"/admin/api/usage/export?format=csv&tenantId={tenantId}");
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
    public async Task ExportUsage_Json_ReturnsSeededRollups()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = Guid.NewGuid();
        await SeedRollupsAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync($"/admin/api/usage/export?format=json&tenantId={tenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"modelId\": \"gpt-4o\"");
        body.Should().Contain("\"costCenter\": \"eng\"");
    }

    private static async Task SeedRollupsAsync(WebApplicationFactory<Program> factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rollups = scope.ServiceProvider.GetRequiredService<IDailyUsageRollupRepository>();
        await rollups.UpsertRollupsAsync([
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
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
}
