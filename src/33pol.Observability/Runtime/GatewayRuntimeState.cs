using System.Collections.Concurrent;
using Pol33.Core.Models;

namespace Pol33.Observability.Runtime;

public sealed class GatewayRuntimeState
{
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    private readonly ConcurrentQueue<RecentRequestEntry> _recentRequests = new();
    private readonly object _statsSync = new();
    private long _totalRequests;
    private long _totalErrors;
    private long _totalLatencyMs;
    private int _activeStreams;
    private long _rateLimitRejections;
    private long _quotaRejections;
    private readonly ConcurrentDictionary<string, long> _requestsPerModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _errorsPerModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _quotaUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _quotaCommits = new(StringComparer.Ordinal);

    public int MaxRecentRequests { get; set; } = 500;

    public void RecordRequestComplete(
        string modelId,
        bool success,
        double durationMs,
        bool wasStreaming)
    {
        lock (_statsSync)
        {
            _totalRequests++;
            _totalLatencyMs += (long)Math.Round(durationMs);
            if (!success)
            {
                _totalErrors++;
            }
        }

        _requestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
        if (!success)
        {
            _errorsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
        }

        if (wasStreaming)
        {
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    public void RecordRequestStart(bool isStreaming)
    {
        if (isStreaming)
        {
            Interlocked.Increment(ref _activeStreams);
        }
    }

    public void RecordRateLimitRejection() => Interlocked.Increment(ref _rateLimitRejections);

    public void RecordQuotaRejection() => Interlocked.Increment(ref _quotaRejections);

    public long GetQuotaUsage(string partitionKey) =>
        _quotaUsage.GetOrAdd(partitionKey, static _ => 0);

    public void AddQuotaUsage(string partitionKey, long tokens)
    {
        _quotaUsage.AddOrUpdate(partitionKey, tokens, (_, existing) => existing + tokens);
    }

    public bool TryCommitQuota(string requestId)
    {
        lock (_quotaCommits)
        {
            return _quotaCommits.Add(requestId);
        }
    }

    public void EnqueueRecent(RecentRequestEntry entry)
    {
        _recentRequests.Enqueue(entry);
        while (_recentRequests.Count > MaxRecentRequests &&
               _recentRequests.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<RecentRequestEntry> GetRecent(int limit)
    {
        var take = Math.Clamp(limit, 1, MaxRecentRequests);
        return _recentRequests.Reverse().Take(take).ToList();
    }

    public (long Total, long Errors, double AvgMs, int ActiveStreams, long RateLimit, long Quota) GetStats()
    {
        lock (_statsSync)
        {
            var avg = _totalRequests == 0 ? 0 : (double)_totalLatencyMs / _totalRequests;
            return (_totalRequests, _totalErrors, avg, Volatile.Read(ref _activeStreams),
                Interlocked.Read(ref _rateLimitRejections), Interlocked.Read(ref _quotaRejections));
        }
    }

    public IReadOnlyDictionary<string, long> GetRequestsPerModel() =>
        new Dictionary<string, long>(_requestsPerModel, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, long> GetErrorsPerModel() =>
        new Dictionary<string, long>(_errorsPerModel, StringComparer.OrdinalIgnoreCase);
}
