using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Publishes what rate limiting is currently doing: the adaptive factor in force per model, and how
/// full the partition tables are.
/// </summary>
/// <remarks>
/// <para>Observable gauges rather than counters, because both are states rather than events. They
/// are read at scrape time, so an idle gateway costs nothing and a busy one pays once per scrape
/// rather than once per request.</para>
///
/// <para>Model is the only label used here. It is bounded by the registry, whereas tenant and — far
/// worse — the anonymous partition key are not: a per-partition gauge would mint one time series per
/// client address block, which is how a metrics backend is taken down. Per-tenant and per-key
/// numbers live in the usage report instead, where the key set is explicitly bounded.</para>
/// </remarks>
public sealed class GatewayRateLimitMetricsExporter(
    IAdaptiveRateLimitGovernor? governor = null,
    IDistributedRateLimitStore? store = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_rate_limit_adaptive_factor",
            ObserveAdaptiveFactors,
            description: "Multiplier applied to a model's configured rate limit (1 = enforced as configured)");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_rate_limit_partitions",
            ObservePartitions,
            description: "Live rate-limit partitions per dimension against the configured ceiling");

        GatewayMeters.Meter.CreateObservableGauge(
            "gateway_rate_limit_backed_off_partitions",
            ObserveBackoff,
            description: "Partitions currently being told to wait longer than their bucket alone would say");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<Measurement<double>> ObserveAdaptiveFactors()
    {
        var snapshot = governor?.Snapshot();
        if (snapshot is null || !snapshot.Enabled)
        {
            yield break;
        }

        foreach (var model in snapshot.Models)
        {
            yield return new Measurement<double>(
                model.Factor,
                new KeyValuePair<string, object?>("model", model.ModelId));
        }
    }

    private IEnumerable<Measurement<int>> ObservePartitions()
    {
        if (store is null)
        {
            yield break;
        }

        var stats = store.GetStats();
        yield return new Measurement<int>(
            stats.RequestPartitions,
            new KeyValuePair<string, object?>("dimension", "request"));
        yield return new Measurement<int>(
            stats.StreamPartitions,
            new KeyValuePair<string, object?>("dimension", "stream"));

        // The ceiling as its own series so an alert can be written on the ratio without the
        // threshold being duplicated into the alert rule, where it would drift from the config.
        yield return new Measurement<int>(
            stats.MaxPartitions,
            new KeyValuePair<string, object?>("dimension", "ceiling"));
    }

    private IEnumerable<Measurement<int>> ObserveBackoff()
    {
        var snapshot = governor?.Snapshot();
        if (snapshot is null || !snapshot.Enabled)
        {
            yield break;
        }

        yield return new Measurement<int>(snapshot.BackedOffPartitions);
    }
}
