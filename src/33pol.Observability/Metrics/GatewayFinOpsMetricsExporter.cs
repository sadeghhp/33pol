using Microsoft.Extensions.Hosting;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Registers the FinOps gauges once per process. The values come from
/// <see cref="GatewayFinOpsMetrics"/>, which the overview refresh publishes from the same snapshot
/// the admin console already builds. With no database that snapshot stays absent and these series
/// are not exported.
/// </summary>
public sealed class GatewayFinOpsMetricsExporter : IHostedService
{
    private static int _registered;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return Task.CompletedTask;
        }

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_billing_cost_dollars",
            GatewayFinOpsMetrics.ObserveSpend,
            description: "Spend from the billing rollups for the current UTC day and the month to date");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_unpriced_models",
            GatewayFinOpsMetrics.ObserveUnpriced,
            description: "Registered models with no active rate card");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_anonymous_request_share",
            GatewayFinOpsMetrics.ObserveAnonymousShare,
            description: "Share of month-to-date requests sent without an API key");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_api_keys",
            GatewayFinOpsMetrics.ObserveKeyCounts,
            description: "Active, expiring, and idle API key counts");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_budget_spend_ratio",
            GatewayFinOpsMetrics.ObserveBudgets,
            description: "Budget (spent + outstanding) divided by its limit");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_quota_used_ratio",
            GatewayFinOpsMetrics.ObserveQuotas,
            description: "Monthly token quota used divided by its limit, per tenant");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_tenant_cost_month_to_date_dollars",
            GatewayFinOpsMetrics.ObserveTenantCosts,
            description: "Month-to-date spend of the top tenant consumers");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_api_key_cost_dollars",
            GatewayFinOpsMetrics.ObserveKeyCosts,
            description: "Month-to-date spend of the top API keys by cost. The long tail is not exported.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
