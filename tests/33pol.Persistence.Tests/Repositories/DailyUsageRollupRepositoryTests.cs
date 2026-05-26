using Pol33.Core.Billing;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class DailyUsageRollupRepositoryTests
{
    [Fact]
    public async Task UpsertRollupsAsync_InsertsAndUpdatesByKey()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpsertRollupsAsync_InsertsAndUpdatesByKey));
        var repository = new DailyUsageRollupRepository(db);
        var tenantId = Guid.NewGuid();
        var usageDate = new DateOnly(2026, 5, 26);

        await repository.UpsertRollupsAsync([
            new DailyUsageRollupRecord(usageDate, tenantId, "gpt-4o", "eng", 100, 50, 0.10m, 1),
        ]);

        await repository.UpsertRollupsAsync([
            new DailyUsageRollupRecord(usageDate, tenantId, "gpt-4o", "eng", 300, 150, 0.30m, 3),
        ]);

        var rollups = await repository.GetRollupsAsync(usageDate, usageDate, tenantId);

        rollups.Should().ContainSingle();
        rollups[0].PromptTokens.Should().Be(300);
        rollups[0].RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task GetRollupsAsync_FiltersByDateRangeAndTenant()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetRollupsAsync_FiltersByDateRangeAndTenant));
        var repository = new DailyUsageRollupRepository(db);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await repository.UpsertRollupsAsync([
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 25), tenantA, "gpt-4o", null, 1, 1, 0.01m, 1),
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantA, "gpt-4o", null, 2, 2, 0.02m, 1),
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantB, "gpt-4o", null, 3, 3, 0.03m, 1),
        ]);

        var rollups = await repository.GetRollupsAsync(
            new DateOnly(2026, 5, 26),
            new DateOnly(2026, 5, 26),
            tenantA);

        rollups.Should().ContainSingle();
        rollups[0].PromptTokens.Should().Be(2);
    }
}
