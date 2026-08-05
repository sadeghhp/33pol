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

    /// <summary>
    /// Captures the process-lifetime counters for durable persistence. Uptime (<see cref="StartedUtc"/>)
    /// and active streams are intentionally excluded — both should reset with the process.
    /// </summary>
    public GatewayRuntimeSnapshot Export()
    {
        lock (_statsSync)
        {
            return new GatewayRuntimeSnapshot
            {
                TotalRequests = _totalRequests,
                TotalErrors = _totalErrors,
                TotalLatencyMs = _totalLatencyMs,
                RateLimitRejections = Interlocked.Read(ref _rateLimitRejections),
                QuotaRejections = Interlocked.Read(ref _quotaRejections),
                RequestsPerModel = new Dictionary<string, long>(_requestsPerModel, StringComparer.OrdinalIgnoreCase),
                ErrorsPerModel = new Dictionary<string, long>(_errorsPerModel, StringComparer.OrdinalIgnoreCase),
                Recent = _recentRequests.ToList(),
            };
        }
    }

    /// <summary>
    /// Seeds the counters from a persisted snapshot so live recording continues from the restored
    /// totals (absolute, not delta — subsequent increments add on top). Call once at startup before
    /// traffic is recorded.
    /// </summary>
    public void Hydrate(GatewayRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_statsSync)
        {
            _totalRequests = snapshot.TotalRequests;
            _totalErrors = snapshot.TotalErrors;
            _totalLatencyMs = snapshot.TotalLatencyMs;
            Interlocked.Exchange(ref _rateLimitRejections, snapshot.RateLimitRejections);
            Interlocked.Exchange(ref _quotaRejections, snapshot.QuotaRejections);

            _requestsPerModel.Clear();
            foreach (var (model, count) in snapshot.RequestsPerModel)
            {
                _requestsPerModel[model] = count;
            }

            _errorsPerModel.Clear();
            foreach (var (model, count) in snapshot.ErrorsPerModel)
            {
                _errorsPerModel[model] = count;
            }

            while (_recentRequests.TryDequeue(out _))
            {
            }

            // Snapshot.Recent is oldest-first; enqueue in order so the queue's ordering is preserved
            // and EnqueueRecent's newest-first read continues to work.
            foreach (var entry in snapshot.Recent)
            {
                EnqueueRecent(entry);
            }
        }
    }
}
