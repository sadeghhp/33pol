using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;
using Pol33.Persistence;

namespace Pol33.Integration.Tests.Phase5;

public sealed class AdminApiKeyMetadataIntegrationTests
{
    [Fact]
    public async Task CreateKey_WithMetadata_ReturnsFieldsOnList()
    {
        const string adminKey = "sk-33pol-api-key-metadata-admin";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory, adminKey);

        var createResponse = await client.PostAsJsonAsync(
            "/admin/api/keys",
            new
            {
                role = "Inference",
                label = "prod-bot",
                assignee = "Platform team",
                costCenter = "eng-platform",
            });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<CreatedKeyDto>();
        created.Should().NotBeNull();

        var listResponse = await client.GetAsync("/admin/api/keys?includeUsageSummary=true");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<List<AdminApiKeyListItemDto>>();
        list.Should().NotBeNull();

        var item = list!.Single(k => k.Id == created!.Id);
        item.Label.Should().Be("prod-bot");
        item.Assignee.Should().Be("Platform team");
        item.CostCenter.Should().Be("eng-platform");
    }

    [Fact]
    public async Task PatchKey_UpdatesMetadata()
    {
        const string adminKey = "sk-33pol-api-key-metadata-patch";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory, adminKey);

        var createResponse = await client.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedKeyDto>();

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/admin/api/keys/{created!.Id}")
        {
            Content = JsonContent.Create(new { assignee = "Data team", label = "etl" }),
        };
        var patchResponse = await client.SendAsync(patchRequest);
        patchResponse.EnsureSuccessStatusCode();

        var updated = await patchResponse.Content.ReadFromJsonAsync<AdminApiKeyListItemDto>();
        updated!.Assignee.Should().Be("Data team");
        updated.Label.Should().Be("etl");
    }

    [Fact]
    public async Task GetUsageEvents_FiltersByApiKeyId()
    {
        const string adminKey = "sk-33pol-api-key-metadata-usage";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = await GetBootstrapTenantIdAsync(factory);
        var keyA = Guid.NewGuid();
        var keyB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using (var scope = factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IBillingEventRepository>();
            await events.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-a",
                tenantId,
                keyA,
                "gpt-4o",
                "eng",
                10,
                5,
                null,
                null,
                0.01m,
                100,
                DateTimeOffset.UtcNow));
            await events.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-b",
                tenantId,
                keyB,
                "gpt-4o",
                "ops",
                20,
                10,
                null,
                null,
                0.02m,
                200,
                DateTimeOffset.UtcNow));
        }

        var client = CreateAdminClient(factory, adminKey);
        var response = await client.GetAsync(
            $"/admin/api/usage/events?tenantId={tenantId}&apiKeyId={keyA}&from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<BillingEventsPageDto>();
        page!.Events.Should().ContainSingle();
        page.Events[0].ApiKeyId.Should().Be(keyA);
    }

    private static async Task<Guid> GetBootstrapTenantIdAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync();
        return tenant.Id;
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory, string adminKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        return client;
    }

    private sealed class CreatedKeyDto
    {
        public Guid Id { get; init; }

        public string? Label { get; init; }

        public string? Assignee { get; init; }

        public string? CostCenter { get; init; }
    }

    private sealed class AdminApiKeyListItemDto
    {
        public Guid Id { get; init; }

        public string? Label { get; init; }

        public string? Assignee { get; init; }

        public string? CostCenter { get; init; }
    }

    private sealed class BillingEventsPageDto
    {
        public List<BillingEventDto> Events { get; init; } = [];

        public int Limit { get; init; }
    }

    private sealed class BillingEventDto
    {
        public Guid? ApiKeyId { get; init; }
    }
}
