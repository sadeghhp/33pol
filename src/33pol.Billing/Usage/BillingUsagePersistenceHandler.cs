using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
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
    BillingBudgetWarningTracker warningTracker) : IUsagePersistenceHandler
{
    public ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default) =>
        PersistBatchAsync([usageEvent], cancellationToken);

    public async Task PersistBatchAsync(
        IReadOnlyList<UsageEvent> usageEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvents);

        foreach (var usageEvent in usageEvents)
        {
            await PersistOneAsync(usageEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistOneAsync(UsageEvent usageEvent, CancellationToken cancellationToken)
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
            }
        }

        var record = BillingEventFactory.FromUsageEvent(usageEvent, costs);
        if (!await billingEvents.TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return;
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

        if (record.TenantId is Guid tenantId)
        {
            await CheckBudgetWarningsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
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

    internal static DateOnly GetPeriodStart(DateOnly today, int periodStartDay)
    {
        var day = Math.Clamp(periodStartDay, 1, 28);
        var candidate = new DateOnly(today.Year, today.Month, Math.Min(day, DateTime.DaysInMonth(today.Year, today.Month)));
        return candidate > today ? candidate.AddMonths(-1) : candidate;
    }
}
