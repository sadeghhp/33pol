using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingBudgetEnforcementService(
    IServiceScopeFactory scopeFactory,
    BudgetReservationLedger reservationLedger,
    IOptions<BillingOptions> billingOptions) : IBudgetEnforcementService
{
    public async ValueTask<BudgetCheckResult> CheckBeforeForwardAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tenantId, out var parsedTenantId))
        {
            return BudgetCheckResult.Allowed;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var budgets = scope.ServiceProvider.GetService<IBudgetRepository>();
        var rollups = scope.ServiceProvider.GetService<IDailyUsageRollupRepository>();
        if (budgets is null || rollups is null)
        {
            return BudgetCheckResult.Allowed;
        }

        var hardBudgets = await GetHardBudgetsAsync(budgets, parsedTenantId, cancellationToken).ConfigureAwait(false);
        if (hardBudgets.Count == 0)
        {
            return BudgetCheckResult.Allowed;
        }

        // Count in-flight reservations so this cheap pre-check reflects concurrent requests whose
        // actual cost has not yet been persisted, not just the (lagging) persisted rollups.
        var outstanding = reservationLedger.GetOutstanding(parsedTenantId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var budget in hardBudgets)
        {
            var spend = await GetPeriodSpendAsync(rollups, parsedTenantId, budget, today, cancellationToken)
                .ConfigureAwait(false);
            if (spend + outstanding >= budget.AmountLimit)
            {
                return BudgetCheckResult.HardExceeded(budget.Name);
            }
        }

        return BudgetCheckResult.Allowed;
    }

    public async ValueTask<BudgetCheckResult> TryReserveAsync(
        string? tenantId,
        string requestId,
        string canonicalModelId,
        long? requestedMaxTokens,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !Guid.TryParse(tenantId, out var parsedTenantId))
        {
            return BudgetCheckResult.Allowed;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var budgets = scope.ServiceProvider.GetService<IBudgetRepository>();
        var rollups = scope.ServiceProvider.GetService<IDailyUsageRollupRepository>();
        if (budgets is null || rollups is null)
        {
            return BudgetCheckResult.Allowed;
        }

        var hardBudgets = await GetHardBudgetsAsync(budgets, parsedTenantId, cancellationToken).ConfigureAwait(false);
        if (hardBudgets.Count == 0)
        {
            return BudgetCheckResult.Allowed;
        }

        // headroom = the smallest remaining allowance across all hard-stop budgets (net of persisted
        // spend). The ledger atomically checks outstanding + estimate against this, so concurrent
        // reservations cannot collectively exceed the tightest budget.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var headroom = decimal.MaxValue;
        var tightestBudgetName = hardBudgets[0].Name;
        foreach (var budget in hardBudgets)
        {
            var spend = await GetPeriodSpendAsync(rollups, parsedTenantId, budget, today, cancellationToken)
                .ConfigureAwait(false);
            var remaining = budget.AmountLimit - spend;
            if (remaining < headroom)
            {
                headroom = remaining;
                tightestBudgetName = budget.Name;
            }
        }

        // An already-exhausted budget blocks regardless of the estimate. Without this, an unpriced
        // model (estimate 0) would sail past a hard stop that persisted spend had already breached —
        // the case the now-removed QuotaMiddleware pre-check used to catch. Reserving must subsume
        // that check, or removing the duplicate would have weakened enforcement.
        if (headroom <= 0m)
        {
            return BudgetCheckResult.HardExceeded(tightestBudgetName);
        }

        var estimate = await EstimateMaxCostAsync(scope, canonicalModelId, requestedMaxTokens, cancellationToken)
            .ConfigureAwait(false);

        return reservationLedger.TryReserve(requestId, parsedTenantId, estimate, headroom)
            ? BudgetCheckResult.Allowed
            : BudgetCheckResult.HardExceeded(tightestBudgetName);
    }

    public void ReleaseReservation(string requestId) => reservationLedger.Release(requestId);

    private static async Task<List<BudgetRecord>> GetHardBudgetsAsync(
        IBudgetRepository budgets,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenantBudgets = await budgets.GetByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return tenantBudgets.Where(b => b.HardStopEnabled).ToList();
    }

    private static async Task<decimal> GetPeriodSpendAsync(
        IDailyUsageRollupRepository rollups,
        Guid tenantId,
        BudgetRecord budget,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var periodStart = BillingUsagePersistenceHandler.GetPeriodStart(today, budget.PeriodStartDay);
        var periodRollups = await rollups
            .GetRollupsAsync(periodStart, today, tenantId, cancellationToken)
            .ConfigureAwait(false);
        return periodRollups.Sum(r => r.TotalCost);
    }

    private async Task<decimal> EstimateMaxCostAsync(
        AsyncServiceScope scope,
        string canonicalModelId,
        long? requestedMaxTokens,
        CancellationToken cancellationToken)
    {
        var rateCards = scope.ServiceProvider.GetService<IRateCardRepository>();
        if (rateCards is null || string.IsNullOrWhiteSpace(canonicalModelId))
        {
            return 0m; // unpriced: cannot estimate, so do not block
        }

        var rateCard = await rateCards
            .GetActiveForModelAsync(canonicalModelId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (rateCard is null)
        {
            return 0m;
        }

        var maxTokens = requestedMaxTokens is > 0
            ? requestedMaxTokens.Value
            : billingOptions.Value.BudgetReservationDefaultMaxTokens;

        // Conservative upper bound: price the estimated token ceiling at the higher of the input/output
        // rate. Actual (usually lower) cost replaces the reservation once the request is persisted.
        var pricePerMillion = Math.Max(rateCard.InputPricePerMillionTokens, rateCard.OutputPricePerMillionTokens);
        return decimal.Round(maxTokens / 1_000_000m * pricePerMillion, 6);
    }
}
