using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingBudgetEnforcementServiceTests
{
    [Fact]
    public async Task CheckBeforeForwardAsync_HardStopOverLimit_ReturnsExceeded()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(),
                    tenantId,
                    "Cap",
                    100m,
                    "USD",
                    0.8m,
                    HardStopEnabled: true,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 100m, 1),
            ]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);
        var result = await service.CheckBeforeForwardAsync(tenantId.ToString());

        result.IsAllowed.Should().BeFalse();
        result.BudgetName.Should().Be("Cap");
    }

    [Fact]
    public async Task CheckBeforeForwardAsync_HardStopAtExactLimit_ReturnsExceeded()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(),
                    tenantId,
                    "Cap",
                    100m,
                    "USD",
                    0.8m,
                    HardStopEnabled: true,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 100m, 1),
            ]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);
        var result = await service.CheckBeforeForwardAsync(tenantId.ToString());

        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckBeforeForwardAsync_InvalidTenantId_ReturnsAllowed()
    {
        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(
            Substitute.For<IBudgetRepository>(),
            Substitute.For<IDailyUsageRollupRepository>());

        var result = await service.CheckBeforeForwardAsync("not-a-guid");

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckBeforeForwardAsync_WhenRepositoriesNotRegistered_ReturnsAllowed()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var service = new BillingBudgetEnforcementService(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await service.CheckBeforeForwardAsync(Guid.NewGuid().ToString());

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckBeforeForwardAsync_HardStopUnderLimit_ReturnsAllowed()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(),
                    tenantId,
                    "Cap",
                    100m,
                    "USD",
                    0.8m,
                    HardStopEnabled: true,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 50m, 1),
            ]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);
        var result = await service.CheckBeforeForwardAsync(tenantId.ToString());

        result.IsAllowed.Should().BeTrue();
    }
}
