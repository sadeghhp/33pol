using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Reconciliation;

/// <summary>
/// Periodically reconciles the billing ledger against the daily usage rollups and reports the result
/// to logs and metrics.
/// </summary>
/// <remarks>
/// The sweep is read-only. It deliberately does not repair what it finds: a discrepancy means one of
/// the two sides is wrong and the job cannot tell which, so silently rewriting rollups from the
/// ledger would destroy the evidence needed to find the cause — and would itself be undetectable if
/// the ledger were the side at fault. Reporting is the whole job.
/// </remarks>
public sealed class BillingReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ILogger<BillingReconciliationHostedService> logger) : BackgroundService
{
    /// <summary>Discrepancies written out individually before the rest are summarised.</summary>
    private const int MaxLoggedDiscrepancies = 20;

    private bool _metricsSinkWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ReconciliationEnabled)
        {
            logger.LogInformation(
                "Billing reconciliation is disabled; divergence between the billing ledger and the "
                + "daily usage rollups will not be detected.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReconciliationIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            // Delay first: at startup the previous day's rollups may still be settling, and a sweep
            // during host construction competes with migration and registry load for the same SQLite
            // connection.
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Billing reconciliation sweep failed");
            }
        }
    }

    internal async Task<BillingReconciliationReport> RunOnceAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var billing = options.Value;
        var toDate = DateOnly.FromDateTime(utcNow.UtcDateTime).AddDays(-1);

        // Never reach past retention: pruned ledger days have surviving rollups and would every one
        // of them report as a discrepancy, drowning any real finding.
        var lookbackDays = Math.Clamp(
            billing.ReconciliationLookbackDays,
            1,
            Math.Max(1, billing.UsageRetentionDays - 1));

        var fromDate = toDate.AddDays(-(lookbackDays - 1));

        await using var scope = scopeFactory.CreateAsyncScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<IBillingReconciliationService>();

        var report = await reconciler.ReconcileAsync(fromDate, toDate, cancellationToken).ConfigureAwait(false);

        Report(report);
        PublishMetrics(scope.ServiceProvider, report);

        return report;
    }

    /// <summary>
    /// Publishes the sweep result to metrics, resolved from the scope rather than injected.
    /// </summary>
    /// <remarks>
    /// The metrics collector lives in the observability module, which this one does not reference. A
    /// constructor dependency on it therefore made <c>AddGatewayBillingPersistence</c> throw at
    /// startup for any composition that did not also add observability — billing cannot require a
    /// module it cannot see. Its absence is reported rather than tolerated silently: the alert on
    /// these metrics is the entire point of the sweep, so running it with nowhere to publish is
    /// close to not running it at all.
    /// </remarks>
    private void PublishMetrics(IServiceProvider services, BillingReconciliationReport report)
    {
        var metrics = services.GetService<IGatewayMetricsCollector>();
        if (metrics is null)
        {
            if (!_metricsSinkWarned)
            {
                _metricsSinkWarned = true;
                logger.LogWarning(
                    "No metrics collector is registered; billing reconciliation results will appear "
                    + "only in these logs. Alerting on gateway_billing_reconciliation_discrepancies "
                    + "will not work.");
            }

            return;
        }

        metrics.RecordBillingReconciliation(report.Discrepancies.Count, (double)report.AbsoluteCostDrift);
    }

    private void Report(BillingReconciliationReport report)
    {
        if (report.IsBalanced)
        {
            logger.LogInformation(
                "Billing reconciliation {From}..{To}: balanced across {Buckets} bucket(s), {Cost} total cost.",
                report.FromDate,
                report.ToDate,
                report.BucketsCompared,
                report.EventTotals.TotalCost);
            return;
        }

        // Warning, not error: the gateway is serving correctly and the operator is the one who has to
        // act. Escalation belongs to whatever is alerting on gateway_billing_reconciliation_discrepancies.
        logger.LogWarning(
            "Billing reconciliation {From}..{To}: {Count} of {Buckets} bucket(s) disagree with the "
            + "billing ledger. Absolute cost drift {AbsoluteDrift}, net {NetDrift} "
            + "(ledger {LedgerCost} vs rollups {RollupCost}). The ledger is authoritative; rollups "
            + "are what the admin UI, budgets and daily webhooks read.",
            report.FromDate,
            report.ToDate,
            report.Discrepancies.Count,
            report.BucketsCompared,
            report.AbsoluteCostDrift,
            report.NetCostDrift,
            report.EventTotals.TotalCost,
            report.RollupTotals.TotalCost);

        foreach (var discrepancy in report.Discrepancies.Take(MaxLoggedDiscrepancies))
        {
            logger.LogWarning(
                "Billing reconciliation {Kind} on {Date} tenant={Tenant} model={Model} costCenter={CostCenter}: "
                + "ledger {LedgerRequests} req / {LedgerTokens} tok / {LedgerCost}; "
                + "rollup {RollupRequests} req / {RollupTokens} tok / {RollupCost}.",
                discrepancy.Kind,
                discrepancy.Key.UsageDate,
                discrepancy.Key.TenantId,
                discrepancy.Key.ModelId,
                discrepancy.Key.CostCenter ?? "-",
                discrepancy.Events.RequestCount,
                discrepancy.Events.TotalTokens,
                discrepancy.Events.TotalCost,
                discrepancy.Rollup.RequestCount,
                discrepancy.Rollup.TotalTokens,
                discrepancy.Rollup.TotalCost);
        }

        if (report.Discrepancies.Count > MaxLoggedDiscrepancies)
        {
            // No admin endpoint exposes the full report: it spans every tenant, while the admin usage
            // surface is tenant-scoped, so serving it there would hand one tenant another's model
            // ids, volumes and costs. Logs and metrics are the operator-level channel for it.
            logger.LogWarning(
                "{Remaining} further reconciliation discrepancy/discrepancies not logged individually; "
                + "the totals above cover all of them.",
                report.Discrepancies.Count - MaxLoggedDiscrepancies);
        }
    }
}
