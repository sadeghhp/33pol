using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// Budget enforcement sits on the inference hot path, so its database cost per request is a
/// behavioural property worth pinning. It used to run twice — once as QuotaMiddleware's
/// CheckBeforeForwardAsync pre-check and again as the router's TryReserveAsync — duplicating the
/// budget query and the period-spend scan for a single decision.
/// </summary>
public sealed class BudgetEnforcementQueryCostTests
{
    private static BudgetRecord HardBudget(Guid tenantId, decimal limit, string name = "Cap") =>
        new(Guid.NewGuid(), tenantId, name, limit, "USD", 0.8m, HardStopEnabled: true, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>
    /// One reservation reads the budget list once and the period spend once per budget. The removed
    /// pre-check doubled both.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_QueriesBudgetsOnceAndSpendOncePerBudget()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([HardBudget(tenantId, 100m)]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 10m, 1)]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);

        var result = await service.TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", 100);

        result.IsAllowed.Should().BeTrue();
        await budgets.Received(1).GetByTenantAsync(tenantId, Arg.Any<CancellationToken>());
        await rollups.Received(1).GetRollupsAsync(
            Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A tenant with no hard budgets must short-circuit before touching spend at all — the common
    /// case for most traffic.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_NoHardBudgets_DoesNotQuerySpend()
    {
        var tenantId = Guid.NewGuid();

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);

        (await service.TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", 100)).IsAllowed.Should().BeTrue();

        await rollups.DidNotReceive().GetRollupsAsync(
            Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The pre-check caught one case reservation did not: an unpriced model (estimate 0) against a
    /// budget already exhausted by persisted spend. Removing the pre-check would have weakened
    /// enforcement without this guard.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_ExhaustedBudgetWithUnpricedModel_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([HardBudget(tenantId, 100m, "Monthly")]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "unpriced", null, 0, 0, 100m, 1)]);

        // No IRateCardRepository registered => estimate is 0 (cannot price).
        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);

        var result = await service.TryReserveAsync(tenantId.ToString(), "req-1", "unpriced", 100);

        result.IsAllowed.Should().BeFalse();
        result.BudgetName.Should().Be("Monthly");
    }

    [Fact]
    public async Task TryReserveAsync_SpendBeyondLimit_IsRejected()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([HardBudget(tenantId, 100m, "Monthly")]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 250m, 1)]);

        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups);

        (await service.TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", 100)).IsAllowed.Should().BeFalse();
    }

    /// <summary>
    /// Hard-stop behaviour must survive concurrency: reservations are what close the window between
    /// admitting a request and persisting its cost.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_ConcurrentReservations_CannotCollectivelyExceedTheBudget()
    {
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([HardBudget(tenantId, 100m)]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 0m, 0)]);

        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync("gpt-4o", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new RateCardRecord(
                Guid.NewGuid(), "card", "Card", "gpt-4o",
                InputPricePerMillionTokens: 10m,
                OutputPricePerMillionTokens: 40m,
                Currency: "USD",
                EffectiveFrom: DateTimeOffset.UtcNow.AddDays(-1),
                EffectiveUntil: null,
                IsActive: true,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow));

        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(10));
        var service = BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups, ledger, rateCards);

        // 1,000,000 tokens at the conservative 40/M rate reserves 40 per request against a limit of
        // 100, so at most two can be in flight at once.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            service.TryReserveAsync(tenantId.ToString(), $"req-{i}", "gpt-4o", 1_000_000).AsTask()));

        results.Count(r => r.IsAllowed).Should().Be(2);
        ledger.GetOutstanding(tenantId).Should().BeLessThanOrEqualTo(100m);
    }
}
