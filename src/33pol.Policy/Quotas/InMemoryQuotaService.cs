using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Policy.Quotas;

public sealed class InMemoryQuotaService(
    IGatewayConfigProvider configProvider,
    IOptions<QuotaOptions> options,
    IGatewayMetricsCollector metricsCollector,
    Func<DateTimeOffset>? clock = null) : IQuotaService, IQuotaUsageSnapshotSource
{
    // Usage is scoped to the current billing month so a "monthly" limit actually resets at the UTC
    // month boundary. Previously usage accumulated for the process lifetime, so once a partition
    // crossed the limit it was hard-blocked forever (until restart) regardless of month rollover.
    private readonly ConcurrentDictionary<string, PeriodUsage> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _committedRequestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _committedRequestOrder = new();
    private readonly object _commitSync = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);

    public QuotaCheckResult CheckBeforeForward(string partitionKey, string modelId)
    {
        _ = modelId;

        // Read the snapshot once: Current can swap mid-method, so pin it for a consistent decision.
        var quota = configProvider.Current.Quota;
        var limit = quota.DefaultMonthlyTokenLimit;
        if (limit <= 0)
        {
            return QuotaCheckResult.Allowed;
        }

        var period = CurrentPeriod();
        var used = _usage.TryGetValue(partitionKey, out var entry) && entry.Period == period
            ? entry.Used
            : 0;

        if (used >= limit)
        {
            metricsCollector.RecordQuotaRejection();
            return QuotaCheckResult.HardExceeded;
        }

        var softThreshold = (long)(limit * quota.SoftLimitRatio);
        if (used >= softThreshold)
        {
            return QuotaCheckResult.SoftWarning(
                $"Monthly token usage at {used} of {limit} ({quota.SoftLimitRatio:P0} soft threshold).");
        }

        return QuotaCheckResult.Allowed;
    }

    public void CommitUsage(string partitionKey, string modelId, long totalTokens, string requestId)
    {
        _ = modelId;
        if (totalTokens <= 0)
        {
            return;
        }

        lock (_commitSync)
        {
            if (!_committedRequestIds.Add(requestId))
            {
                return;
            }

            _committedRequestOrder.Enqueue(requestId);
            TrimCommittedRequestIdsIfNeeded();
        }

        var period = CurrentPeriod();
        _usage.AddOrUpdate(
            partitionKey,
            _ => new PeriodUsage(period, totalTokens),
            (_, existing) => existing.Period == period
                ? existing with { Used = existing.Used + totalTokens }
                : new PeriodUsage(period, totalTokens));
    }

    public IReadOnlyList<QuotaUsageSnapshot> ExportUsage() =>
        _usage
            .Select(kvp => new QuotaUsageSnapshot(kvp.Key, kvp.Value.Period, kvp.Value.Used))
            .ToList();

    public void HydrateUsage(IReadOnlyList<QuotaUsageSnapshot> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        var period = CurrentPeriod();
        foreach (var usage in usages)
        {
            // Ignore stale months so a partition's prior-period usage never suppresses the current
            // month's fresh allowance.
            if (!string.Equals(usage.Period, period, StringComparison.Ordinal))
            {
                continue;
            }

            _usage[usage.PartitionKey] = new PeriodUsage(usage.Period, usage.Used);
        }
    }

    private string CurrentPeriod() => _clock().UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private void TrimCommittedRequestIdsIfNeeded()
    {
        var retentionLimit = Math.Max(1, options.Value.CommittedRequestIdRetentionLimit);
        while (_committedRequestIds.Count > retentionLimit && _committedRequestOrder.TryDequeue(out var oldest))
        {
            _committedRequestIds.Remove(oldest);
        }
    }

    private readonly record struct PeriodUsage(string Period, long Used);
}
