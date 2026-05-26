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
}
