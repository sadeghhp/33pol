using System.Net;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminUsageIntegrationTests
{
    [Fact]
    public async Task GetUsage_WithAdminKey_ReturnsReportShape()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await response.Content.ReadFromJsonAsync<UsageReportDto>();
        report.Should().NotBeNull();
        report!.Summary.Should().NotBeNull();
        report.Rollups.Should().NotBeNull();
    }

    [Fact]
    public async Task ExportUsage_Csv_ReturnsAttachment()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/export?format=csv");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("usage_date,tenant_id");
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
    }
}
