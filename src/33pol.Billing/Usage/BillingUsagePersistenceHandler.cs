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
    IDailyUsageRollupAggregator aggregator,
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

    public async Task PersistBatchAsync(
        IReadOnlyList<UsageEvent> usageEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvents);

        var updatedDays = new HashSet<(Guid TenantId, DateOnly UsageDate)>();
        foreach (var usageEvent in usageEvents)
        {
            var updated = await PersistOneAsync(usageEvent, cancellationToken).ConfigureAwait(false);
            if (updated is not null)
            {
                updatedDays.Add(updated.Value);
            }
        }

        foreach (var (tenantId, usageDate) in updatedDays)
        {
            await DispatchDailyUsageAsync(tenantId, usageDate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(Guid TenantId, DateOnly UsageDate)?> PersistOneAsync(
        UsageEvent usageEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        var atUtc = usageEvent.TimestampUtc == default ? DateTimeOffset.UtcNow : usageEvent.TimestampUtc;
        BillingCostBreakdown? costs = null;
        if (!string.IsNullOrWhiteSpace(usageEvent.ModelId))
        {
            var rateCard = await rateCards
                .GetActiveForModelAsync(usageEvent.ModelId, atUtc, cancellationToken)
                .ConfigureAwait(false);
            if (rateCard is not null)
            {
                costs = costCalculator.Calculate(
                    rateCard,
                    usageEvent.PromptTokens,
                    usageEvent.CompletionTokens);
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

        var record = BillingEventFactory.FromUsageEvent(usageEvent, costs);
        if (!await billingEvents.TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
        {
            // Duplicate: the actual cost was already persisted for this request, so free its reservation.
            reservationLedger.Release(usageEvent.RequestId);
            return null;
        }

        if (record.ApiKeyId is Guid apiKeyId)
        {
            await lastUsedTracker.TouchAsync(apiKeyId, record.RecordedAt, cancellationToken).ConfigureAwait(false);
        }

        var usageDate = DateOnly.FromDateTime(record.RecordedAt.UtcDateTime);
        var existingRollups = await rollups
            .GetRollupsAsync(usageDate, usageDate, record.TenantId, cancellationToken)
            .ConfigureAwait(false);

        var existing = existingRollups.FirstOrDefault(r =>
            string.Equals(r.ModelId, record.ModelId, StringComparison.Ordinal) &&
            string.Equals(r.CostCenter, record.CostCenter, StringComparison.Ordinal));

        DailyUsageRollupRecord merged;
        if (existing is null)
        {
            merged = aggregator.Aggregate([record]).Single();
        }
        else
        {
            merged = existing with
            {
                PromptTokens = existing.PromptTokens + record.PromptTokens,
                CompletionTokens = existing.CompletionTokens + record.CompletionTokens,
                TotalCost = existing.TotalCost + (record.TotalCost ?? 0m),
                RequestCount = existing.RequestCount + 1,
            };
        }

        await rollups.UpsertRollupsAsync([merged], cancellationToken).ConfigureAwait(false);

        // Actual cost is now in the rollups; release the in-flight reservation (no accounting gap
        // between reservation and persisted spend).
        reservationLedger.Release(usageEvent.RequestId);

        if (record.TenantId is not Guid tenantId)
        {
            return null;
        }

        await CheckBudgetWarningsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return (tenantId, usageDate);
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
