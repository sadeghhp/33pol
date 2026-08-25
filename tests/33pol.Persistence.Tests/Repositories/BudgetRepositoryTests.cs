using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class BudgetRepositoryTests
{
    [Fact]
    public async Task GetByTenantAsync_ReturnsOnlyMatchingTenant()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetByTenantAsync_ReturnsOnlyMatchingTenant));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.Budgets.AddRange(
            new BudgetEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                Name = "A",
                AmountLimit = 100m,
                Currency = "USD",
                WarningThresholdRatio = 0.8m,
                HardStopEnabled = false,
                PeriodStartDay = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new BudgetEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                Name = "B",
                AmountLimit = 200m,
                Currency = "USD",
                WarningThresholdRatio = 0.8m,
                HardStopEnabled = true,
                PeriodStartDay = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var repository = new BudgetRepository(db);
        var budgets = await repository.GetByTenantAsync(tenantA);

        budgets.Should().ContainSingle();
        budgets[0].Name.Should().Be("A");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEveryTenantsBudgetsOrderedByTenantThenName()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetAllAsync_ReturnsEveryTenantsBudgetsOrderedByTenantThenName));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        BudgetEntity Budget(Guid tenant, string name) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Name = name,
            AmountLimit = 100m,
            Currency = "USD",
            WarningThresholdRatio = 0.8m,
            HardStopEnabled = false,
            PeriodStartDay = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Budgets.AddRange(Budget(tenantB, "Z"), Budget(tenantA, "B"), Budget(tenantA, "A"));
        await db.SaveChangesAsync();

        var all = await new BudgetRepository(db).GetAllAsync();

        all.Should().HaveCount(3);
        all.Select(b => b.TenantId).Distinct().Should().BeEquivalentTo([tenantA, tenantB]);
        all.Where(b => b.TenantId == tenantA).Select(b => b.Name).Should().Equal("A", "B");
    }
}
