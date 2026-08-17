using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Usage;

public sealed class BillingUsagePersistenceHandler(
    IBillingEventRepository billingEvents,
    IDailyUsageRollupRepository rollups,
    IRateCardRepository rateCards,
    IRateCardCostCalculator costCalculator,
    IBudgetRepository budgets,
    IBillingWebhookDispatcher webhooks,
    BillingBudgetWarningTracker warningTracker,
    BillingUnpricedModelTracker unpricedModelTracker,
    IApiKeyLastUsedTracker lastUsedTracker,
    BudgetReservationLedger reservationLedger,
    ILogger<BillingUsagePersistenceHandler> logger,
    // Optional so the handler still composes without the observability layer (unit tests, tools).
    // When present, every priced event is echoed to the console's live feed.
    IRecentRequestStore? recentRequests = null) : IUsagePersistenceHandler
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
        // They are written as one batch — one existence probe and one transaction — rather than one
        // SaveChanges per event, which had turned a 100-event batch into 200 round trips and 100
        // separate WAL commits and made the usage writer the slowest stage in the process.
        var records = new List<BillingEventRecord>(usageEvents.Count);
        foreach (var usageEvent in usageEvents)
        {
            records.Add(BillingEventFactory.FromUsageEvent(usageEvent, PriceEvent(usageEvent, pricedByModel)));
        }

        var appended = await AppendAsync(records, cancellationToken).ConfigureAwait(false);

        // Priced now, and the write is behind us: tell the live feed. Duplicates included — the same
        // request id was already stored at this cost, and the operator's row should not sit on
        // "pending" because a retry happened to flush first.
        PublishToLiveFeed(usageEvents, records, pricedByModel);
        if (appended.Count != records.Count)
        {
            // Duplicates: the cost was already persisted for those requests, so free their
            // reservations and keep them out of the rollup aggregation.
            var appendedIds = new HashSet<string>(appended.Select(r => r.RequestId), StringComparer.Ordinal);
            foreach (var record in records)
            {
                if (!appendedIds.Contains(record.RequestId))
                {
                    reservationLedger.Release(record.RequestId);
                }
            }
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

        try
        {
            await rollups.IncrementRollupsAsync(deltas, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The billing_events rows are already committed but their spend never reached the
            // rollups, so budgets will under-count these requests permanently. Surface that
            // explicitly rather than letting the caller's catch-all log it as a generic failure —
            // and still release the reservations, which would otherwise sit until their TTL.
            logger.LogError(
                ex,
                "Rollup increment failed for {EventCount} persisted billing event(s) across {BucketCount} "
                + "bucket(s). Their spend is recorded in billing_events but is NOT reflected in "
                + "daily_usage_rollups, so budget and quota totals will under-count until the rollups "
                + "are rebuilt from billing_events.",
                appended.Count,
                deltas.Count);

            ReleaseReservations(appended);
            throw;
        }

        // Actual cost is now in the rollups; release the in-flight reservations (no accounting gap
        // between reservation and persisted spend).
        ReleaseReservations(appended);

        var tenantIds = appended
            .Where(r => r.TenantId is not null)
            .Select(r => r.TenantId!.Value)
            .Distinct()
            .ToList();

        foreach (var tenantId in tenantIds)
        {
            await CheckBudgetWarningsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }

        // The daily usage summary is NOT dispatched here. It is owned by
        // DailyUsageWebhookPublisher, which runs after the day closes and reports the day's totals.
        // Firing it inline sent a "daily" summary containing whatever had accrued by a tenant's
        // first request of the day — and, because both paths share the same dedup tracker, that
        // send consumed the tracker slot so the real end-of-day summary was never delivered at all.
    }

    private void PublishToLiveFeed(
        IReadOnlyList<UsageEvent> usageEvents,
        IReadOnlyList<BillingEventRecord> records,
        Dictionary<string, RateCardRecord?> pricedByModel)
    {
        if (recentRequests is null)
        {
            return;
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var source = usageEvents[i];
            var currency = pricedByModel.TryGetValue(source.ModelId, out var rateCard) ? rateCard?.Currency : null;
            recentRequests.AttachUsage(record.RequestId, RecentRequestUsageMapper.FromBillingEvent(record, source, currency));
        }
    }

    private async Task<IReadOnlyList<BillingEventRecord>> AppendAsync(
        IReadOnlyList<BillingEventRecord> records,
        CancellationToken cancellationToken)
    {
        if (billingEvents is IBillingEventBatchAppender batchAppender)
        {
            return await batchAppender.TryAppendManyAsync(records, cancellationToken).ConfigureAwait(false);
        }

        var appended = new List<BillingEventRecord>(records.Count);
        foreach (var record in records)
        {
            if (await billingEvents.TryAppendAsync(record, cancellationToken).ConfigureAwait(false))
            {
                appended.Add(record);
            }
        }

        return appended;
    }

    private void ReleaseReservations(IReadOnlyList<BillingEventRecord> appended)
    {
        foreach (var record in appended)
        {
            reservationLedger.Release(record.RequestId);
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

            // Release the once-per-period reservation if delivery never succeeds, so a transient
            // receiver outage does not permanently swallow this period's warning.
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
                onPermanentFailure: () => warningTracker.Release(key),
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
