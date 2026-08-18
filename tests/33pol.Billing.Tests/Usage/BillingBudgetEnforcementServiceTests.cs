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
    public async Task TryReserveAsync_ConcurrentReservationsExceedingBudget_SecondIsRejected()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(Guid.NewGuid(), tenantId, "Cap", 100m, "USD", 0.8m,
                    HardStopEnabled: true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([]); // no persisted spend yet

        // Rate card prices tokens so 4096 default tokens => an estimate near the whole budget.
        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync("gpt-4o", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new RateCardRecord(Guid.NewGuid(), "rc", "RC", "gpt-4o",
                InputPricePerMillionTokens: 0m, OutputPricePerMillionTokens: 20_000m, "USD",
                DateTimeOffset.UtcNow, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(2));
        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups, ledger, rateCards);

        // Each reservation estimates 4096 / 1e6 * 20000 = ~81.9 => two cannot both fit under 100.
        var first = await service.TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", null, requestBodyBytes: 0);
        var second = await service.TryReserveAsync(tenantId.ToString(), "req-2", "gpt-4o", null, requestBodyBytes: 0);

        first.IsAllowed.Should().BeTrue();
        second.IsAllowed.Should().BeFalse();

        // Releasing the first frees headroom for a subsequent request.
        service.ReleaseReservation("req-1");
        var third = await service.TryReserveAsync(tenantId.ToString(), "req-3", "gpt-4o", null, requestBodyBytes: 0);
        third.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserveAsync_NoRateCard_DoesNotBlock()
    {
        var tenantId = Guid.NewGuid();
        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(Guid.NewGuid(), tenantId, "Cap", 100m, "USD", 0.8m,
                    HardStopEnabled: true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([]);
        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((RateCardRecord?)null);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(
            budgets, rollups, new BudgetReservationLedger(TimeSpan.FromMinutes(2)), rateCards);

        (await service.TryReserveAsync(tenantId.ToString(), "req-1", "unpriced", 4096, requestBodyBytes: 0)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckBeforeForwardAsync_WhenRepositoriesNotRegistered_ReturnsAllowed()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var service = new BillingBudgetEnforcementService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BudgetReservationLedger(TimeSpan.FromMinutes(2)),
            new BudgetSpendCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            Microsoft.Extensions.Options.Options.Create(new Pol33.Core.Configuration.BillingOptions()));

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
