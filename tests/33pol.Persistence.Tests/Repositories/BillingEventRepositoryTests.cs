using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class BillingEventRepositoryTests
{
    [Fact]
    public async Task GetUsageSummariesAsync_GroupsByApiKeyId()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetUsageSummariesAsync_GroupsByApiKeyId));
        var sut = new BillingEventRepository(db);
        var tenantId = Guid.NewGuid();
        var keyA = Guid.NewGuid();
        var keyB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var at = DateTimeOffset.UtcNow;

        await sut.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(), "req-1", tenantId, keyA, "gpt-4o", "eng", 10, 5, null, null, 0.10m, 100, at));
        await sut.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(), "req-2", tenantId, keyA, "gpt-4o", "eng", 20, 10, null, null, 0.20m, 200, at));
        await sut.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(), "req-3", tenantId, keyB, "gpt-4o", "ops", 5, 2, null, null, 0.05m, 50, at));

        var summaries = await sut.GetUsageSummariesAsync(tenantId, today, today);

        summaries.Should().HaveCount(2);
        summaries[keyA].RequestCount.Should().Be(2);
        summaries[keyA].PromptTokens.Should().Be(30);
        summaries[keyA].TotalCost.Should().Be(0.30m);
        summaries[keyB].RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task QueryAsync_FiltersByApiKeyIdAndCostCenter()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_FiltersByApiKeyIdAndCostCenter));
        var sut = new BillingEventRepository(db);
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var at = DateTimeOffset.UtcNow;

        await sut.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(), "req-a", tenantId, keyId, "gpt-4o", "eng", 10, 5, null, null, 0.10m, 100, at));
        await sut.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(), "req-b", tenantId, Guid.NewGuid(), "gpt-4o", "ops", 5, 2, null, null, 0.05m, 50, at));

        var events = await sut.QueryAsync(new BillingEventQuery(
            FromDate: today,
            ToDate: today,
            TenantId: tenantId,
            ApiKeyId: keyId,
            CostCenter: "eng"));

        events.Should().ContainSingle();
        events[0].ApiKeyId.Should().Be(keyId);
        events[0].CostCenter.Should().Be("eng");
    }
}
