using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Reconciliation;

/// <summary>
/// Compares the billing event ledger against the daily usage rollups derived from it.
/// </summary>
/// <remarks>
/// <para>Every number an operator sees — the admin usage pages, budget enforcement, the daily
/// summary webhook — is read from the rollups, while the ledger is what actually records a request.
/// A defect anywhere between the two therefore produces plausible, wrong numbers and no error: the
/// dashboards stay green while the money is wrong. Several such defects have already been found by
/// reading the code (usage that never parsed, a rollup write that failed after the ledger append,
/// totals summed in floating point); this exists so the next one is found by the gateway instead.</para>
///
/// <para>Costs are compared exactly, with no tolerance. Both sides are decimal sums of the same
/// per-request values, so correct code agrees to the last digit — and any tolerance wide enough to
/// absorb a rounding difference is also wide enough to hide a genuinely dropped request.</para>
/// </remarks>
public sealed class BillingReconciliationService(
    IBillingEventRepository billingEvents,
    IDailyUsageRollupRepository rollups) : IBillingReconciliationService
{
    public async Task<BillingReconciliationReport> ReconcileAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        if (toDate < fromDate)
        {
            return BillingReconciliationReport.Empty(fromDate, toDate);
        }

        var ledgerTotals = await billingEvents
            .GetDailyTotalsAsync(fromDate, toDate, cancellationToken)
            .ConfigureAwait(false);

        var storedRollups = await rollups
            .GetRollupsAsync(fromDate, toDate, tenantId: null, cancellationToken)
            .ConfigureAwait(false);

        var ledgerByKey = ToBuckets(ledgerTotals);
        var rollupByKey = ToBuckets(storedRollups);

        var discrepancies = new List<BillingReconciliationDiscrepancy>();
        var eventTotals = BillingReconciliationTotals.Zero;
        var rollupSideTotals = BillingReconciliationTotals.Zero;

        foreach (var (key, ledger) in ledgerByKey)
        {
            eventTotals += ledger;

            if (!rollupByKey.TryGetValue(key, out var rollup))
            {
                discrepancies.Add(new BillingReconciliationDiscrepancy(
                    BillingReconciliationKind.MissingFromRollups,
                    key,
                    ledger,
                    BillingReconciliationTotals.Zero));
                continue;
            }

            if (!Matches(ledger, rollup))
            {
                discrepancies.Add(new BillingReconciliationDiscrepancy(
                    BillingReconciliationKind.TotalsDiffer,
                    key,
                    ledger,
                    rollup));
            }
        }

        foreach (var (key, rollup) in rollupByKey)
        {
            rollupSideTotals += rollup;

            if (!ledgerByKey.ContainsKey(key))
            {
                discrepancies.Add(new BillingReconciliationDiscrepancy(
                    BillingReconciliationKind.MissingFromEvents,
                    key,
                    BillingReconciliationTotals.Zero,
                    rollup));
            }
        }

        // Worst first, so a truncated log or a webhook payload carries the discrepancies that matter.
        discrepancies.Sort(static (left, right) =>
            Math.Abs(right.CostDelta).CompareTo(Math.Abs(left.CostDelta)));

        var comparedKeys = new HashSet<DailyUsageRollupKey>(ledgerByKey.Keys);
        comparedKeys.UnionWith(rollupByKey.Keys);

        return new BillingReconciliationReport(
            fromDate,
            toDate,
            comparedKeys.Count,
            eventTotals,
            rollupSideTotals,
            discrepancies);
    }

    private static bool Matches(BillingReconciliationTotals ledger, BillingReconciliationTotals rollup) =>
        ledger.PromptTokens == rollup.PromptTokens &&
        ledger.CompletionTokens == rollup.CompletionTokens &&
        ledger.RequestCount == rollup.RequestCount &&
        ledger.TotalCost == rollup.TotalCost;

    private static Dictionary<DailyUsageRollupKey, BillingReconciliationTotals> ToBuckets(
        IReadOnlyList<DailyUsageRollupRecord> records)
    {
        var buckets = new Dictionary<DailyUsageRollupKey, BillingReconciliationTotals>();

        foreach (var record in records)
        {
            var key = DailyUsageRollupKey.FromRecord(record);
            var totals = new BillingReconciliationTotals(
                record.PromptTokens,
                record.CompletionTokens,
                record.TotalCost,
                record.RequestCount);

            // Defensive: the rollup table is keyed on exactly this tuple, so duplicates should be
            // impossible. Summing rather than overwriting means that if they ever do occur, the
            // report shows the real stored total instead of silently reporting only the last row.
            buckets[key] = buckets.TryGetValue(key, out var existing) ? existing + totals : totals;
        }

        return buckets;
    }
}
