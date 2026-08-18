using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Usage;

public sealed class BillingBudgetEnforcementService(
    IServiceScopeFactory scopeFactory,
    BudgetReservationLedger reservationLedger,
    BudgetSpendCache spendCache,
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
        long requestBodyBytes,
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

        var estimate = await EstimateMaxCostAsync(
                scope,
                canonicalModelId,
                requestedMaxTokens,
                requestBodyBytes,
                cancellationToken)
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

    /// <summary>
    /// Period-to-date spend for one budget, cached briefly per (tenant, period).
    /// </summary>
    /// <remarks>
    /// This runs on the inference hot path — once per budget, per request — and each call scanned
    /// every rollup row in the billing period and summed them in memory. The cache is safe against
    /// overshoot because it only covers <em>persisted</em> spend: cost incurred since the last read
    /// is held by the reservation ledger, which is exact and consulted separately — and the usage
    /// writer invalidates the tenant's entries (<see cref="BudgetSpendCache.Invalidate"/>) before it
    /// releases the reservations of a persisted batch, so there is no window in which spend is
    /// counted by neither. The TTL bounds staleness for spend written by another replica.
    /// </remarks>
    private async Task<decimal> GetPeriodSpendAsync(
        IDailyUsageRollupRepository rollups,
        Guid tenantId,
        BudgetRecord budget,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var periodStart = BillingUsagePersistenceHandler.GetPeriodStart(today, budget.PeriodStartDay);
        if (spendCache.TryGet(tenantId, periodStart, today, out var cached, out var generation))
        {
            return cached;
        }

        var periodRollups = await rollups
            .GetRollupsAsync(periodStart, today, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var spend = periodRollups.Sum(r => r.TotalCost);

        spendCache.Set(
            tenantId,
            periodStart,
            today,
            spend,
            TimeSpan.FromSeconds(Math.Max(1, billingOptions.Value.BudgetSpendCacheTtlSeconds)),
            generation);
        return spend;
    }

    /// <summary>
    /// Conservative upper bound on what the request can cost: the prompt priced at the input rate,
    /// plus the output ceiling priced at the higher of the two rates.
    /// </summary>
    /// <remarks>
    /// The prompt term is what makes a hard stop hold for long-context traffic. Pricing only
    /// <paramref name="requestedMaxTokens"/> meant the input — the dominant cost of a
    /// retrieval-augmented or long-context call — was reserved as zero, so concurrent large-prompt
    /// requests could each reserve a few thousand output tokens while collectively incurring millions
    /// of input tokens against a cap the ledger believed was untouched. The prompt estimate is
    /// derived from the body actually being forwarded, and the actual (usually lower) cost replaces
    /// the whole reservation once the request is persisted.
    /// </remarks>
    private async Task<decimal> EstimateMaxCostAsync(
        AsyncServiceScope scope,
        string canonicalModelId,
        long? requestedMaxTokens,
        long requestBodyBytes,
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

        var maxOutputTokens = requestedMaxTokens is > 0
            ? requestedMaxTokens.Value
            : billingOptions.Value.BudgetReservationDefaultMaxTokens;

        // The output split is unknown until the response returns, so it is priced at the dearer rate.
        var outputPricePerMillion = Math.Max(
            rateCard.InputPricePerMillionTokens,
            rateCard.OutputPricePerMillionTokens);

        var promptTokens = UsageEventFactory.EstimatePromptTokens(requestBodyBytes);
        var estimate = (promptTokens / 1_000_000m * rateCard.InputPricePerMillionTokens)
            + (maxOutputTokens / 1_000_000m * outputPricePerMillion);

        return decimal.Round(estimate, 6);
    }
}
