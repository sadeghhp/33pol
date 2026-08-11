using NSubstitute;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// The reservation must price the prompt, not just the requested output ceiling.
/// </summary>
/// <remarks>
/// Reserving only <c>max_tokens</c> left the input unpriced, and for long-context traffic the input
/// is the dominant cost. Concurrent large-prompt requests could each reserve a few thousand output
/// tokens while collectively incurring millions of input tokens against a hard cap the ledger
/// believed was untouched — which is exactly the overshoot the ledger exists to prevent.
/// </remarks>
public sealed class BudgetReservationPromptCostTests
{
    private const decimal InputPricePerMillion = 10m;
    private const decimal OutputPricePerMillion = 40m;

    [Fact]
    public async Task TryReserveAsync_LargePrompt_ReservesTheInputCostToo()
    {
        var tenantId = Guid.NewGuid();
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(10));
        var service = CreateService(tenantId, limit: 10_000m, ledger);

        // 40 MB of prompt ~ 10M estimated input tokens at 10/M = 100.
        const long promptBytes = 40L * 1024 * 1024;
        var expectedPromptCost = UsageEventFactoryPromptCost(promptBytes, InputPricePerMillion);

        var result = await service.TryReserveAsync(
            tenantId.ToString(), "req-1", "gpt-4o", requestedMaxTokens: 100, requestBodyBytes: promptBytes);

        result.IsAllowed.Should().BeTrue();
        ledger.GetOutstanding(tenantId).Should().BeGreaterThanOrEqualTo(expectedPromptCost);
    }

    [Fact]
    public async Task TryReserveAsync_SamePromptWithAndWithoutBody_DiffersByTheInputCost()
    {
        var tenantId = Guid.NewGuid();

        var withoutPrompt = new BudgetReservationLedger(TimeSpan.FromMinutes(10));
        await CreateService(tenantId, 10_000m, withoutPrompt)
            .TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", 100, requestBodyBytes: 0);

        var withPrompt = new BudgetReservationLedger(TimeSpan.FromMinutes(10));
        await CreateService(tenantId, 10_000m, withPrompt)
            .TryReserveAsync(tenantId.ToString(), "req-1", "gpt-4o", 100, requestBodyBytes: 4L * 1024 * 1024);

        withPrompt.GetOutstanding(tenantId)
            .Should().BeGreaterThan(withoutPrompt.GetOutstanding(tenantId));
    }

    /// <summary>
    /// The scenario the gap allowed: a fleet of long-context requests, each declaring a small
    /// <c>max_tokens</c>, sailing past a hard cap because only the output was reserved.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_ConcurrentLargePromptRequests_CannotCollectivelyExceedTheBudget()
    {
        var tenantId = Guid.NewGuid();
        var ledger = new BudgetReservationLedger(TimeSpan.FromMinutes(10));
        var service = CreateService(tenantId, limit: 100m, ledger);

        // 4 MB of prompt ~ 1M estimated input tokens at 10/M = ~10 per request, so a limit of 100
        // admits roughly ten of them and no more.
        const long promptBytes = 4L * 1024 * 1024;
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            service.TryReserveAsync(tenantId.ToString(), $"req-{i}", "gpt-4o", 16, promptBytes).AsTask()));

        results.Count(r => r.IsAllowed).Should().BeLessThan(
            40,
            "the input side of a long-context request has to count against the cap");
        ledger.GetOutstanding(tenantId).Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public async Task TryReserveAsync_UnpricedModel_StillDoesNotBlockOnPromptSize()
    {
        var tenantId = Guid.NewGuid();
        var service = CreateService(tenantId, limit: 100m, new BudgetReservationLedger(TimeSpan.FromMinutes(10)),
            registerRateCard: false);

        var result = await service.TryReserveAsync(
            tenantId.ToString(), "req-1", "unpriced", 100, requestBodyBytes: 40L * 1024 * 1024);

        result.IsAllowed.Should().BeTrue("an unpriced model cannot be estimated, so it must not block");
    }

    private static decimal UsageEventFactoryPromptCost(long bytes, decimal pricePerMillion) =>
        Pol33.Core.Usage.UsageEventFactory.EstimatePromptTokens(bytes) / 1_000_000m * pricePerMillion;

    private static BillingBudgetEnforcementService CreateService(
        Guid tenantId,
        decimal limit,
        BudgetReservationLedger ledger,
        bool registerRateCard = true)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([
                new BudgetRecord(
                    Guid.NewGuid(), tenantId, "Cap", limit, "USD", 0.8m, HardStopEnabled: true, 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), tenantId, Arg.Any<CancellationToken>())
            .Returns([new DailyUsageRollupRecord(today, tenantId, "gpt-4o", null, 0, 0, 0m, 0)]);

        IRateCardRepository? rateCards = null;
        if (registerRateCard)
        {
            rateCards = Substitute.For<IRateCardRepository>();
            rateCards.GetActiveForModelAsync("gpt-4o", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new RateCardRecord(
                    Guid.NewGuid(), "card", "Card", "gpt-4o",
                    InputPricePerMillionTokens: InputPricePerMillion,
                    OutputPricePerMillionTokens: OutputPricePerMillion,
                    Currency: "USD",
                    EffectiveFrom: DateTimeOffset.UtcNow.AddDays(-1),
                    EffectiveUntil: null,
                    IsActive: true,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow));
        }

        return BillingBudgetEnforcementServiceTestsHelper.CreateService(budgets, rollups, ledger, rateCards);
    }
}
