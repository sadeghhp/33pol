using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Policy.Quotas;

public sealed class InMemoryQuotaService(
    IOptions<QuotaOptions> options,
    IGatewayMetricsCollector metricsCollector) : IQuotaService
{
    private readonly ConcurrentDictionary<string, long> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _committedRequestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _committedRequestOrder = new();
    private readonly object _commitSync = new();

    public QuotaCheckResult CheckBeforeForward(string partitionKey, string modelId)
    {
        _ = modelId;
        var limit = options.Value.DefaultMonthlyTokenLimit;
        if (limit <= 0)
        {
            return QuotaCheckResult.Allowed;
        }

        var used = _usage.GetOrAdd(partitionKey, static _ => 0);
        if (used >= limit)
        {
            metricsCollector.RecordQuotaRejection();
            return QuotaCheckResult.HardExceeded;
        }

        var softThreshold = (long)(limit * options.Value.SoftLimitRatio);
        if (used >= softThreshold)
        {
            return QuotaCheckResult.SoftWarning(
                $"Monthly token usage at {used} of {limit} ({options.Value.SoftLimitRatio:P0} soft threshold).");
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

        _usage.AddOrUpdate(partitionKey, totalTokens, (_, existing) => existing + totalTokens);
    }

    private void TrimCommittedRequestIdsIfNeeded()
    {
        var retentionLimit = Math.Max(1, options.Value.CommittedRequestIdRetentionLimit);
        while (_committedRequestIds.Count > retentionLimit && _committedRequestOrder.TryDequeue(out var oldest))
        {
            _committedRequestIds.Remove(oldest);
        }
    }
}
