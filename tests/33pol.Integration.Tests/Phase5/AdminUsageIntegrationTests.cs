using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Integration.Tests.Support;
using Pol33.Persistence;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Security;

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
        // Yesterday: the trailing window is made of complete days only, so today's rows do not count.
        await SeedRollupsAsync(factory, tenantId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));

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
    public async Task GetUsage_AnonymousRows_HiddenUnlessIncludeAnonymous()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedRollupsAsync(factory, tenantId);
        await SeedRollupsAsync(factory, null, today, "public-model", requests: 3);
        await SeedRollupsAsync(factory, Guid.NewGuid(), today, "gpt-4o", requests: 9); // another tenant

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var scoped = await client.GetFromJsonAsync<UsageReportDto>("/admin/api/usage");
        var withAnon = await client.GetFromJsonAsync<UsageReportDto>("/admin/api/usage?includeAnonymous=true");

        scoped!.Summary!.TotalRequests.Should().Be(2);
        scoped.Summary.AnonymousRequests.Should().Be(0);
        withAnon!.Summary!.TotalRequests.Should().Be(5);
        withAnon.Summary.AnonymousRequests.Should().Be(3);
        withAnon.Rollups.Should().HaveCount(2);
        withAnon.UnpricedModelIds.Should().BeEquivalentTo("gpt-4o", "public-model");
        withAnon.Currency.Should().Be("USD");
        withAnon.Source.Should().Be("rollups");

        var events = await client.GetFromJsonAsync<BillingEventsPageDto>("/admin/api/usage/events?includeAnonymous=true");
        events!.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetUsage_CostCenterFilter_IsCaseInsensitive_AndSupportsNoneSentinel()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedRollupsAsync(factory, tenantId);                                  // eng, 2 requests
        await SeedRollupsAsync(factory, tenantId, today, "gpt-4o-mini", requests: 7, costCenter: null);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var upper = await client.GetFromJsonAsync<UsageReportDto>("/admin/api/usage?costCenter=ENG");
        var none = await client.GetFromJsonAsync<UsageReportDto>("/admin/api/usage?costCenter=(none)");

        upper!.Summary!.TotalRequests.Should().Be(2);
        none!.Summary!.TotalRequests.Should().Be(7);
    }

    [Fact]
    public async Task GetUsage_FromAfterTo_ReturnsBadRequest()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        foreach (var route in new[] { "/admin/api/usage", "/admin/api/usage/events", "/admin/api/usage/export?format=csv" })
        {
            var separator = route.Contains('?') ? "&" : "?";
            var response = await client.GetAsync(route + separator + "from=2026-08-18&to=2026-08-01");
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, route);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("invalid_range");
        }
    }

    /// <summary>
    /// Anonymous rows belong to no tenant, so they are operator-level data. A tenant admin outside
    /// the operator tenant asking for them gets its own tenant-scoped report — the flag is ignored,
    /// not rejected — on every usage route.
    /// </summary>
    [Fact]
    public async Task GetUsage_IncludeAnonymous_IsIgnoredForNonOperatorTenantAdmin()
    {
        const string tenantBAdminKey = "sk-33pol-usage-tenant-b-admin";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantBId = await SeedSecondTenantAdminAsync(factory, tenantBAdminKey);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedRollupsAsync(factory, tenantBId, today, "gpt-4o", requests: 4);
        await SeedRollupsAsync(factory, null, today, "public-model", requests: 3);
        await SeedBillingEventAsync(factory, tenantBId, "b-own");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", tenantBAdminKey);

        var report = await client.GetFromJsonAsync<UsageReportDto>("/admin/api/usage?includeAnonymous=true");
        report!.Summary!.TotalRequests.Should().Be(4);
        report.Summary.AnonymousRequests.Should().Be(0);
        report.Rollups.Should().HaveCount(1);

        var events = await client.GetFromJsonAsync<BillingEventsPageDto>("/admin/api/usage/events?includeAnonymous=true");
        events!.Events.Should().OnlyContain(e => e.RequestId == "b-own");

        var forecast = await client.GetAsync("/admin/api/usage/forecast?includeAnonymous=true");
        forecast.StatusCode.Should().Be(HttpStatusCode.OK);

        var export = await client.GetAsync("/admin/api/usage/export?format=csv&includeAnonymous=true");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        (await export.Content.ReadAsStringAsync()).Should().NotContain("public-model");
    }

    [Fact]
    public async Task GetUsage_WithApiKeyId_AggregatesLedgerForThatKeyOnly()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var keyA = Guid.NewGuid();
        var keyB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedBillingEventAsync(factory, tenantId, "a-1", keyA, 0.10m, now);
        await SeedBillingEventAsync(factory, tenantId, "a-2", keyA, 0.20m, now.AddSeconds(-1));
        await SeedBillingEventAsync(factory, tenantId, "b-1", keyB, 5.00m, now.AddSeconds(-2));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var report = await client.GetFromJsonAsync<UsageReportDto>($"/admin/api/usage?apiKeyId={keyA}");

        report!.Source.Should().Be("events");
        report.Summary!.TotalRequests.Should().Be(2);
        report.Summary.TotalCost.Should().Be(0.30m);

        var export = await client.GetAsync($"/admin/api/usage/export?format=csv&apiKeyId={keyA}");
        var csv = await export.Content.ReadAsStringAsync();
        csv.Should().Contain(",0.30,").And.NotContain("5.00");
    }

    [Fact]
    public async Task GetEvents_Paginates_WithCursor()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await SeedBillingEventAsync(factory, tenantId, "p-" + i, null, 0.01m, now.AddSeconds(-i));
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var first = await client.GetFromJsonAsync<BillingEventsPageDto>("/admin/api/usage/events?limit=2");
        first!.Events.Should().HaveCount(2);
        first.HasMore.Should().BeTrue();
        first.NextCursor.Should().NotBeNullOrEmpty();

        var second = await client.GetFromJsonAsync<BillingEventsPageDto>(
            "/admin/api/usage/events?limit=2&cursor=" + Uri.EscapeDataString(first.NextCursor!));
        second!.Events.Should().HaveCount(2);
        second.HasMore.Should().BeTrue();

        var third = await client.GetFromJsonAsync<BillingEventsPageDto>(
            "/admin/api/usage/events?limit=2&cursor=" + Uri.EscapeDataString(second.NextCursor!));
        third!.Events.Should().HaveCount(1);
        third.HasMore.Should().BeFalse();
        third.NextCursor.Should().BeNull();

        var all = first.Events!.Concat(second.Events!).Concat(third.Events!).Select(e => e.RequestId).ToList();
        all.Should().Equal("p-0", "p-1", "p-2", "p-3", "p-4");

        var bad = await client.GetAsync("/admin/api/usage/events?cursor=not-a-cursor");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExportUsage_EventsDataset_ReturnsLedgerCsvWithFilenameAndTruncationHeader()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        await SeedBillingEventAsync(factory, tenantId);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/usage/export?format=csv&dataset=events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("usage-events-");
        response.Headers.GetValues("X-Export-Truncated").Should().Equal("false");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("recorded_at_utc,request_id");
        body.Should().Contain("req-seed-events");

        var invalid = await client.GetAsync("/admin/api/usage/export?format=xml");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

    private static async Task<Guid> SeedSecondTenantAdminAsync(WebApplicationFactory<Program> factory, string apiKey)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "usage-tenant-b",
            Name = "Usage Tenant B",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeyHash = ApiKeyHashing.Hash(apiKey, "integration-test-pepper"),
            KeyPrefix = ApiKeyHashing.CreatePrefix(apiKey),
            Role = ApiKeyRole.Admin,
            Scopes = ["admin"],
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return tenantId;
    }

    private static async Task<Guid> GetBootstrapTenantIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        return tenant.Id;
    }

    private static async Task SeedBillingEventAsync(
        WebApplicationFactory<Program> factory,
        Guid tenantId,
        string requestId = "req-seed-events",
        Guid? apiKeyId = null,
        decimal cost = 0.01m,
        DateTimeOffset? at = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IBillingEventRepository>();
        await events.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(),
            requestId,
            tenantId,
            apiKeyId,
            "gpt-4o",
            "eng",
            10,
            5,
            null,
            null,
            cost,
            100,
            at ?? DateTimeOffset.UtcNow));
    }

    private static async Task SeedRollupsAsync(
        WebApplicationFactory<Program> factory,
        Guid? tenantId,
        DateOnly? usageDate = null,
        string modelId = "gpt-4o",
        int requests = 2,
        string? costCenter = "eng")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var rollups = scope.ServiceProvider.GetRequiredService<IDailyUsageRollupRepository>();
        await rollups.UpsertRollupsAsync([
            new DailyUsageRollupRecord(
                usageDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                tenantId,
                modelId,
                costCenter,
                100,
                50,
                0.15m,
                requests),
        ]);
    }

    private sealed class UsageReportDto
    {
        public UsageSummaryDto? Summary { get; init; }

        public List<object>? Rollups { get; init; }

        public string? Currency { get; init; }

        public string? Source { get; init; }

        public List<string> UnpricedModelIds { get; init; } = [];
    }

    private sealed class UsageSummaryDto
    {
        public long TotalPromptTokens { get; init; }

        public long TotalCompletionTokens { get; init; }

        public decimal TotalCost { get; init; }

        public int TotalRequests { get; init; }

        public int AnonymousRequests { get; init; }
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

        public bool HasMore { get; init; }

        public string? NextCursor { get; init; }
    }

    private sealed class BillingEventDto
    {
        public string? ModelId { get; init; }

        public string? RequestId { get; init; }
    }
}
