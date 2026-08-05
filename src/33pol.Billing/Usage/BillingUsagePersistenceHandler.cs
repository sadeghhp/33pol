using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingUsagePersistenceHandler(
    IBillingEventRepository billingEvents,
    IDailyUsageRollupRepository rollups,
    IRateCardRepository rateCards,
    IRateCardCostCalculator costCalculator,
    IBudgetRepository budgets,
    IBillingWebhookDispatcher webhooks,
    BillingBudgetWarningTracker warningTracker,
    BillingDailyUsageWebhookTracker dailyWebhookTracker,
    BillingUnpricedModelTracker unpricedModelTracker,
    IApiKeyLastUsedTracker lastUsedTracker,
    BudgetReservationLedger reservationLedger,
    ILogger<BillingUsagePersistenceHandler> logger,
    IOptions<BillingOptions> billingOptions) : IUsagePersistenceHandler
{
    public async ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        await PersistBatchAsync([usageEvent], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a whole batch with a per-batch, not per-event, database cost: rate cards are fetched
    /// once, rollup changes are grouped into one atomic increment per bucket, api-key touches are
    /// deduplicated per key, and budget warnings are evaluated once per tenant.
    /// </summary>
    /// <remarks>
    /// This method previously looped calling a single-event path, so a batch of 100 issued roughly
    /// 600 round-trips: a rate-card lookup, an append, a last-used touch, a rollup read, a rollup
    /// write and a budget-warning scan for every event.
    /// </remarks>
    public async Task PersistBatchAsync(
        IReadOnlyList<UsageEvent> usageEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvents);
        if (usageEvents.Count == 0)
        {
            return;
        }

        var pricedByModel = await ResolveRateCardsAsync(usageEvents, cancellationToken).ConfigureAwait(false);

        // Each event still becomes its own billing_events row: that is the audit trail, and its
        // per-request detail (request id, duration, per-event cost) must not be aggregated away.
        var appended = new List<BillingEventRecord>(usageEvents.Count);
        foreach (var usageEvent in usageEvents)
        {
            var record = BillingEventFactory.FromUsageEvent(usageEvent, PriceEvent(usageEvent, pricedByModel));

            if (!await billingEvents.TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
            {
                // Duplicate: the cost was already persisted for this request, so free its
                // reservation and keep it out of the rollup aggregation.
                reservationLedger.Release(usageEvent.RequestId);
                continue;
            }

            appended.Add(record);
        }

        if (appended.Count == 0)
        {
            return;
        }

        // One touch per distinct api key, using its latest timestamp in this batch.
        foreach (var group in appended
                     .Where(r => r.ApiKeyId is not null)
                     .GroupBy(r => r.ApiKeyId!.Value))
        {
            await lastUsedTracker
                .TouchAsync(group.Key, group.Max(r => r.RecordedAt), cancellationToken)
                .ConfigureAwait(false);
        }

        // One additive delta per (day, tenant, model, cost centre) bucket, applied atomically. The
        // old read-add-write of an absolute total could lose a concurrent writer's usage entirely.
        var deltas = appended
            .GroupBy(DailyUsageRollupKey.FromEvent)
            .Select(group => new DailyUsageRollupDelta(
                group.Key.UsageDate,
                group.Key.TenantId,
                group.Key.ModelId,
                group.Key.CostCenter,
                group.Sum(r => r.PromptTokens),
                group.Sum(r => r.CompletionTokens),
                group.Sum(r => r.TotalCost ?? 0m),
                group.Count()))
            .ToList();

        await rollups.IncrementRollupsAsync(deltas, cancellationToken).ConfigureAwait(false);

        // Actual cost is now in the rollups; release the in-flight reservations (no accounting gap
        // between reservation and persisted spend).
        foreach (var record in appended)
        {
            reservationLedger.Release(record.RequestId);
        }

        var tenantDays = appended
            .Where(r => r.TenantId is not null)
            .Select(r => (TenantId: r.TenantId!.Value, UsageDate: DateOnly.FromDateTime(r.RecordedAt.UtcDateTime)))
            .Distinct()
            .ToList();

        foreach (var tenantId in tenantDays.Select(d => d.TenantId).Distinct())
        {
            await CheckBudgetWarningsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (tenantId, usageDate) in tenantDays)
        {
            await DispatchDailyUsageAsync(tenantId, usageDate, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches the active rate card for every distinct model in the batch, once each, and warns once
    /// per unpriced model.
    /// </summary>
    private async Task<Dictionary<string, RateCardRecord?>> ResolveRateCardsAsync(
        IReadOnlyList<UsageEvent> usageEvents,
        CancellationToken cancellationToken)
    {
        var byModel = new Dictionary<string, RateCardRecord?>(StringComparer.OrdinalIgnoreCase);

        foreach (var usageEvent in usageEvents)
        {
            if (string.IsNullOrWhiteSpace(usageEvent.ModelId) || byModel.ContainsKey(usageEvent.ModelId))
            {
                continue;
            }

            var atUtc = usageEvent.TimestampUtc == default ? DateTimeOffset.UtcNow : usageEvent.TimestampUtc;
            var rateCard = await rateCards
                .GetActiveForModelAsync(usageEvent.ModelId, atUtc, cancellationToken)
                .ConfigureAwait(false);

            byModel[usageEvent.ModelId] = rateCard;

            if (rateCard is not null)
            {
                unpricedModelTracker.Clear(usageEvent.ModelId);
            }
            else if (unpricedModelTracker.TryMarkWarned(usageEvent.ModelId))
            {
                // Without a rate card the event persists with null costs and rolls up as zero
                // spend, which is indistinguishable from genuinely free usage. Say so once.
                logger.LogWarning(
                    "Model '{ModelId}' has no rate card; its usage will record as zero cost. " +
                    "Set input/output prices for it in the admin model settings.",
                    usageEvent.ModelId);
            }
        }

        return byModel;
    }

    private BillingCostBreakdown? PriceEvent(
        UsageEvent usageEvent,
        Dictionary<string, RateCardRecord?> pricedByModel)
    {
        if (string.IsNullOrWhiteSpace(usageEvent.ModelId) ||
            !pricedByModel.TryGetValue(usageEvent.ModelId, out var rateCard) ||
            rateCard is null)
        {
            return null;
        }

        // Total-only usage carries no input/output split, so it cannot be priced with the per-side
        // rates; the calculator applies the conservative (dearer-rate) policy.
        return usageEvent.TokenSource == UsageTokenSource.TotalOnly
            ? costCalculator.CalculateFromTotalTokens(rateCard, usageEvent.TotalTokens)
            : costCalculator.Calculate(rateCard, usageEvent.PromptTokens, usageEvent.CompletionTokens);
    }

    private async Task DispatchDailyUsageAsync(
        Guid tenantId,
        DateOnly usageDate,
        CancellationToken cancellationToken)
    {
        if (!dailyWebhookTracker.TryMarkSent(tenantId, usageDate))
        {
            return;
        }

        var dayRollups = await rollups
            .GetRollupsAsync(usageDate, usageDate, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (dayRollups.Count == 0)
        {
            return;
        }

        await webhooks.DispatchAsync(
            "usage.daily",
            new
            {
                tenantId,
                usageDate = usageDate.ToString("O"),
                promptTokens = dayRollups.Sum(r => r.PromptTokens),
                completionTokens = dayRollups.Sum(r => r.CompletionTokens),
                totalCost = dayRollups.Sum(r => r.TotalCost),
                requestCount = dayRollups.Sum(r => r.RequestCount),
                currency = billingOptions.Value.DefaultCurrency,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckBudgetWarningsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenantBudgets = await budgets.GetByTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenantBudgets.Count == 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var budget in tenantBudgets)
        {
            var periodStart = GetPeriodStart(today, budget.PeriodStartDay);
            var periodRollups = await rollups
                .GetRollupsAsync(periodStart, today, tenantId, cancellationToken)
                .ConfigureAwait(false);

            var spend = periodRollups.Sum(r => r.TotalCost);
            var warnAt = budget.AmountLimit * budget.WarningThresholdRatio;
            if (spend < warnAt)
            {
                continue;
            }

            var key = $"{tenantId:N}:{budget.Id:N}:{periodStart:yyyy-MM-dd}";
            if (!warningTracker.TryMarkSent(key))
            {
                continue;
            }

            await webhooks.DispatchAsync(
                "quota.warning",
                new
                {
                    tenantId,
                    budgetId = budget.Id,
                    budgetName = budget.Name,
                    spend,
                    limit = budget.AmountLimit,
                    currency = budget.Currency,
                    periodStart = periodStart.ToString("O"),
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static DateOnly GetPeriodStart(DateOnly today, int periodStartDay)
    {
        var day = Math.Clamp(periodStartDay, 1, 28);
        var candidate = new DateOnly(today.Year, today.Month, Math.Min(day, DateTime.DaysInMonth(today.Year, today.Month)));
        return candidate > today ? candidate.AddMonths(-1) : candidate;
    }
}
