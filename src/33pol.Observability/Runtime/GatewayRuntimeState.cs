using System.Collections.Concurrent;
using Pol33.Core.Models;

namespace Pol33.Observability.Runtime;

public sealed class GatewayRuntimeState
{
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    private readonly ConcurrentQueue<RecentRequestEntry> _recentRequests = new();

    /// <summary>
    /// Requests currently being forwarded, keyed by request id. Bounded by the bulkhead's
    /// per-model concurrency in practice; <see cref="MaxInFlightTracked"/> is the backstop for a
    /// caller that leaks a begin without its matching completion.
    /// </summary>
    private readonly ConcurrentDictionary<string, RecentRequestEntry> _inFlight = new(StringComparer.Ordinal);

    private readonly object _statsSync = new();
    private long _totalRequests;
    private long _totalErrors;
    private long _totalLatencyMs;
    private int _activeStreams;
    private int _activeRequests;
    private long _rateLimitRejections;
    private long _quotaRejections;
    private readonly ConcurrentDictionary<string, long> _requestsPerModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _errorsPerModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeRequestsPerModel = new(StringComparer.OrdinalIgnoreCase);

    public int MaxRecentRequests { get; set; } = 500;

    public int MaxInFlightTracked { get; set; } = 1000;

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

        Interlocked.Decrement(ref _activeRequests);
        DecrementActiveForModel(modelId);
    }

    /// <summary>
    /// Marks a request as in flight. Every admitted request counts toward
    /// <see cref="GetActiveRequests"/>; only streaming ones also count as an active stream.
    /// </summary>
    /// <remarks>
    /// Streaming used to be the only thing counted here, which is why the console showed nothing at
    /// all while a non-streaming completion or embedding was running — the whole request produced
    /// its first and only telemetry after it had already finished.
    /// </remarks>
    public void RecordRequestStart(string modelId, bool isStreaming)
    {
        if (isStreaming)
        {
            Interlocked.Increment(ref _activeStreams);
        }

        Interlocked.Increment(ref _activeRequests);
        _activeRequestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
    }

    /// <summary>
    /// Counts a request the gateway rejected at admission, before any upstream call. Contributes to
    /// the request and error totals but not to latency — see <see cref="Pol33.Core.Abstractions.IRequestTracker.RecordRejectedRequest"/>.
    /// </summary>
    public void RecordRequestRejected(string modelId)
    {
        lock (_statsSync)
        {
            _totalRequests++;
            _totalErrors++;
        }

        _requestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
        _errorsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
    }

    private void DecrementActiveForModel(string modelId)
    {
        // Drop the key at zero rather than leaving a 0 entry: the per-model breakdown is rendered
        // as "what is running right now", and a model with nothing in flight should not appear.
        while (_activeRequestsPerModel.TryGetValue(modelId, out var current))
        {
            if (current <= 1)
            {
                if (_activeRequestsPerModel.TryRemove(new KeyValuePair<string, int>(modelId, current)))
                {
                    return;
                }

                continue;
            }

            if (_activeRequestsPerModel.TryUpdate(modelId, current - 1, current))
            {
                return;
            }
        }
    }

    public void RecordRateLimitRejection() => Interlocked.Increment(ref _rateLimitRejections);

    public void RecordQuotaRejection() => Interlocked.Increment(ref _quotaRejections);

    public void EnqueueRecent(RecentRequestEntry entry)
    {
        // Promoting the finished entry and retiring the in-flight one is a single step, so the feed
        // never shows the same request twice and never blinks it out between the two writes.
        _inFlight.TryRemove(entry.RequestId, out _);

        _recentRequests.Enqueue(entry);
        while (_recentRequests.Count > MaxRecentRequests &&
               _recentRequests.TryDequeue(out _))
        {
        }
    }

    public void BeginInFlight(RecentRequestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_inFlight.Count >= MaxInFlightTracked && !_inFlight.ContainsKey(entry.RequestId))
        {
            return;
        }

        _inFlight[entry.RequestId] = entry with { IsInFlight = true };
    }

    public void CompleteInFlight(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId))
        {
            _inFlight.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// The live feed: everything currently in flight (newest first, with the elapsed time so far),
    /// then completed requests newest first. In-flight rows are stamped with their duration at read
    /// time, which is what makes a running request's timer advance across dashboard polls.
    /// </summary>
    public IReadOnlyList<RecentRequestEntry> GetRecent(int limit)
    {
        var take = Math.Clamp(limit, 1, MaxRecentRequests);
        var now = DateTimeOffset.UtcNow;

        var running = _inFlight.Values
            .OrderByDescending(e => e.TimestampUtc)
            .Take(take)
            .Select(e => e with { DurationMs = Math.Max(0, (now - e.TimestampUtc).TotalMilliseconds) })
            .ToList();

        if (running.Count >= take)
        {
            return running;
        }

        running.AddRange(_recentRequests.Reverse().Take(take - running.Count));
        return running;
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

    public int GetActiveRequests() => Math.Max(0, Volatile.Read(ref _activeRequests));

    public IReadOnlyDictionary<string, int> GetActiveRequestsPerModel() =>
        new Dictionary<string, int>(_activeRequestsPerModel, StringComparer.OrdinalIgnoreCase);

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
