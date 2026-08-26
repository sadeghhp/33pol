using System.Collections.Concurrent;
using Pol33.Core.Models;
using Pol33.Core.Models.Overview;

namespace Pol33.Observability.Runtime;

public sealed class GatewayRuntimeState
{
    public GatewayRuntimeState()
        : this(new RollingWindowStats())
    {
    }

    public GatewayRuntimeState(RollingWindowStats windows)
    {
        Windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The time-bucketed view behind the Overview's windows and sparklines. Fed from the same
    /// calls as the lifetime counters, but never persisted.
    /// </summary>
    public RollingWindowStats Windows { get; }

    /// <summary>Requests per tenant over the last 24 hours, for the Overview's top consumers.</summary>
    public CountDimension TenantRequests { get; } = new();

    /// <summary>Tokens per tenant over the last 24 hours (attached when usage arrives).</summary>
    public CountDimension TenantTokens { get; } = new();

    private readonly ConcurrentQueue<RecentRequestEntry> _recentRequests = new();

    /// <summary>
    /// Requests currently being forwarded, keyed by request id. Bounded by the bulkhead's
    /// per-model concurrency in practice; <see cref="MaxInFlightTracked"/> is the backstop for a
    /// caller that leaks a begin without its matching completion.
    /// </summary>
    private readonly ConcurrentDictionary<string, RecentRequestEntry> _inFlight = new(StringComparer.Ordinal);
    private int _inFlightCount;

    /// <summary>
    /// Priced usage keyed by request id, merged onto feed rows at read time. Rows are immutable
    /// records sitting in a queue that cannot be updated in place, and pricing arrives one flush
    /// interval after the row was written — so the join happens on read rather than on write.
    /// Bounded by <see cref="MaxRecentRequests"/> plus <see cref="MaxInFlightTracked"/>: an id the
    /// feed has already evicted is dropped in insertion order.
    /// </summary>
    private readonly ConcurrentDictionary<string, RecentRequestUsage> _usageByRequest = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _usageOrder = new();

    /// <summary>
    /// Bumped on every change an operator could see on the Overview: admission, completion,
    /// rejection, pricing, resets. The live endpoint pushes a frame when it moves.
    /// </summary>
    private long _version;

    private readonly object _statsSync = new();
    private long _totalRequests;
    private long _totalErrors;
    private long _clientDisconnects;
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
        bool wasStreaming) =>
        RecordRequestComplete(modelId, success, durationMs, wasStreaming, tenantId: null);

    /// <inheritdoc cref="RecordRequestComplete(string, bool, double, bool)"/>
    /// <param name="tenantId">The tenant the request belonged to; null for anonymous traffic.</param>
    public void RecordRequestComplete(
        string modelId,
        bool success,
        double durationMs,
        bool wasStreaming,
        string? tenantId)
    {
        if (!string.IsNullOrEmpty(tenantId))
        {
            TenantRequests.Add(tenantId, DateTimeOffset.UtcNow);
        }

        lock (_statsSync)
        {
            _totalRequests++;
            _totalLatencyMs += (long)Math.Round(durationMs);
            if (!success)
            {
                _totalErrors++;
                // Under the same lock as the total so ResetErrors cannot clear one and miss the
                // other, leaving the per-model sum disagreeing with the pill.
                _errorsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
            }
        }

        _requestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);

        Windows.RecordCompletion(modelId, durationMs, success, wasStreaming);

        if (wasStreaming)
        {
            Interlocked.Decrement(ref _activeStreams);
        }

        Interlocked.Decrement(ref _activeRequests);
        DecrementActiveForModel(modelId);
        Touch();
    }

    /// <summary>
    /// Records a request whose client disconnected before the response finished. It counts toward
    /// the request total and latency like any completion, and toward <c>ClientDisconnects</c>
    /// instead of the error total — the Overview "errors" pill and the Errors tab both measure
    /// failures, and a caller walking away is not one.
    /// </summary>
    public void RecordRequestCanceled(string modelId, double durationMs, bool wasStreaming, string? tenantId)
    {
        if (!string.IsNullOrEmpty(tenantId))
        {
            TenantRequests.Add(tenantId, DateTimeOffset.UtcNow);
        }

        lock (_statsSync)
        {
            _totalRequests++;
            _totalLatencyMs += (long)Math.Round(durationMs);
            _clientDisconnects++;
        }

        _requestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);

        // Not a failure of the backend, so the windowed error rate (and the attention rule built on
        // it) does not move either.
        Windows.RecordCompletion(modelId, durationMs, success: true, wasStreaming);

        if (wasStreaming)
        {
            Interlocked.Decrement(ref _activeStreams);
        }

        Interlocked.Decrement(ref _activeRequests);
        DecrementActiveForModel(modelId);
        Touch();
    }

    /// <summary>Monotonic change counter; see <see cref="_version"/>.</summary>
    public long Version => Interlocked.Read(ref _version);

    private void Touch() => Interlocked.Increment(ref _version);

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
        Touch();
    }

    /// <summary>
    /// Counts a request the gateway rejected at admission, before any upstream call. Contributes to
    /// the request and error totals but not to latency — see <see cref="Pol33.Core.Abstractions.IRequestTracker.RecordRejectedRequest"/>.
    /// </summary>
    public void RecordRequestRejected(string modelId) =>
        RecordRequestRejected(modelId, reason: null);

    /// <inheritdoc cref="RecordRequestRejected(string)"/>
    /// <param name="reason">Which admission control refused the request; null when it is counted under a reason by another call.</param>
    public void RecordRequestRejected(string modelId, RejectionReason? reason)
    {
        lock (_statsSync)
        {
            _totalRequests++;
            _totalErrors++;
            _errorsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
        }

        _requestsPerModel.AddOrUpdate(modelId, 1, static (_, count) => count + 1);
        Windows.RecordRejection(modelId, reason, countAsFailedRequest: true);
        Touch();
    }

    /// <summary>
    /// Time to first token for a streaming response. Only the windowed statistics keep it — there is
    /// no lifetime TTFT counter, and the Prometheus histogram is recorded by the metrics collector.
    /// </summary>
    public void RecordTimeToFirstToken(string modelId, double ttftMs)
    {
        Windows.RecordTimeToFirstToken(modelId, ttftMs);
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

    /// <summary>
    /// Zeroes the error counters and drops failed rows from the recent-request feed, so an operator
    /// who has fixed a problem can watch for its recurrence against a clean baseline.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Total requests, accumulated latency, per-model request counts, live
    /// activity and uptime are all left alone — clearing errors must not silently rewrite the
    /// throughput and latency history alongside them. The visible consequence is that the error rate
    /// reads 0% against a non-zero request total until new traffic arrives, which is exactly what
    /// "the errors were cleared" should look like.
    /// </remarks>
    /// <returns>How many rows were removed from the recent-request feed.</returns>
    public (long TotalErrorsCleared, int RecentRowsRemoved) ResetErrors()
    {
        lock (_statsSync)
        {
            var clearedTotal = _totalErrors;
            _totalErrors = 0;
            _clientDisconnects = 0;
            _errorsPerModel.Clear();
            Windows.ResetErrors();

            // Drain and re-enqueue rather than filter in place: ConcurrentQueue has no removal, and
            // the feed's ordering is load-bearing for the dashboard's newest-first read.
            var kept = new List<RecentRequestEntry>(_recentRequests.Count);
            var removed = 0;
            while (_recentRequests.TryDequeue(out var entry))
            {
                if (entry.StatusCode < 400 && entry.ErrorCode is null)
                {
                    kept.Add(entry);
                }
                else
                {
                    removed++;
                }
            }

            foreach (var entry in kept)
            {
                _recentRequests.Enqueue(entry);
            }

            Touch();
            return (clearedTotal, removed);
        }
    }

    /// <summary>
    /// Zeroes every process-lifetime counter and empties the recent-request feed — the operator's
    /// "start the dashboard over" action, as opposed to <see cref="ResetErrors"/>.
    /// </summary>
    /// <remarks>
    /// Live state is deliberately untouched: active requests and streams describe work currently in
    /// flight, and zeroing them would leave the gauges permanently wrong once those requests
    /// completed and decremented past zero. <see cref="StartedUtc"/> stays too — uptime belongs to
    /// the process, not to the counters.
    /// </remarks>
    public void ResetAll()
    {
        lock (_statsSync)
        {
            _totalRequests = 0;
            _totalErrors = 0;
            _clientDisconnects = 0;
            _totalLatencyMs = 0;
            Interlocked.Exchange(ref _rateLimitRejections, 0);
            Interlocked.Exchange(ref _quotaRejections, 0);
            _requestsPerModel.Clear();
            _errorsPerModel.Clear();

            while (_recentRequests.TryDequeue(out _))
            {
            }

            _usageByRequest.Clear();
            while (_usageOrder.TryDequeue(out _))
            {
            }

            Windows.Reset();
            TenantRequests.Clear();
            TenantTokens.Clear();
            Touch();
        }
    }

    public void RecordRateLimitRejection() => RecordRateLimitRejection(RejectionReason.RateLimit, modelId: null);

    /// <param name="reason">
    /// The specific control that refused the request (<see cref="RejectionReason.RateLimit"/>,
    /// <see cref="RejectionReason.StreamConcurrency"/>, …) — the lifetime counter stays one number;
    /// the windowed statistics keep the breakdown.
    /// </param>
    /// <param name="modelId">Known only for controls that run after routing; null otherwise.</param>
    public void RecordRateLimitRejection(RejectionReason reason, string? modelId)
    {
        Interlocked.Increment(ref _rateLimitRejections);
        // Reason only: the middleware refuses before the request is counted, and the lifetime
        // request/error totals do not include it either.
        Windows.RecordRejection(modelId, reason, countAsFailedRequest: false);
        Touch();
    }

    public void RecordQuotaRejection() => RecordQuotaRejection(RejectionReason.Quota, modelId: null);

    public void RecordQuotaRejection(RejectionReason reason, string? modelId)
    {
        Interlocked.Increment(ref _quotaRejections);
        Windows.RecordRejection(modelId, reason, countAsFailedRequest: false);
        Touch();
    }

    /// <summary>A refusal that is neither a request nor a lifetime counter — only the windowed reason breakdown sees it.</summary>
    public void RecordReasonOnly(RejectionReason reason, string? modelId)
    {
        Windows.RecordRejection(modelId, reason, countAsFailedRequest: false);
        Touch();
    }

    public void EnqueueRecent(RecentRequestEntry entry)
    {
        // Promoting the finished entry and retiring the in-flight one is a single step, so the feed
        // never shows the same request twice and never blinks it out between the two writes.
        if (_inFlight.TryRemove(entry.RequestId, out _))
        {
            Interlocked.Decrement(ref _inFlightCount);
        }

        _recentRequests.Enqueue(entry);
        while (_recentRequests.Count > MaxRecentRequests &&
               _recentRequests.TryDequeue(out var evicted))
        {
            // The row is gone from the feed, so its pricing has nowhere to be merged onto.
            _usageByRequest.TryRemove(evicted.RequestId, out _);
        }

        Touch();
    }

    /// <summary>
    /// Records the priced usage for a request. Merged onto the matching feed row on every read, so
    /// it works whether the row is in flight, completed, or — when the writer wins the race with
    /// the completion — not yet recorded.
    /// </summary>
    public void AttachUsage(string requestId, RecentRequestUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        var added = _usageByRequest.TryAdd(requestId, usage);
        if (added)
        {
            _usageOrder.Enqueue(requestId);
            var cap = MaxRecentRequests + MaxInFlightTracked;
            while (_usageOrder.Count > cap && _usageOrder.TryDequeue(out var oldest))
            {
                _usageByRequest.TryRemove(oldest, out _);
            }
        }
        else
        {
            _usageByRequest[requestId] = usage;
        }

        // Tokens and priced cost feed the windows once per request: the first attach carries the
        // tokens (pending pricing), and only a priced attach carries a cost. Re-pricing the same
        // request would double count, so the tokens are only counted on the first attach.
        // One lookup, not two: finding the row walks the recent-request queue, and this runs once per
        // usage event off the billing writer.
        var entry = FindEntry(requestId);
        var modelId = entry?.ModelId;
        if (added)
        {
            Windows.RecordUsage(modelId, usage.PromptTokens, usage.CompletionTokens, PricedCostOf(usage));
            if (entry?.TenantId is { Length: > 0 } tenantId)
            {
                TenantTokens.Add(tenantId, DateTimeOffset.UtcNow, usage.PromptTokens + usage.CompletionTokens);
            }
        }
        else if (PricedCostOf(usage) is { } cost)
        {
            Windows.RecordUsage(modelId, 0, 0, cost);
        }

        Touch();
    }

    private static decimal? PricedCostOf(RecentRequestUsage usage) =>
        usage.PricingStatus == RecentRequestUsage.StatusPriced ? usage.TotalCost : null;

    /// <summary>
    /// The in-flight or recently completed row for <paramref name="requestId"/>, or null once the
    /// feed has evicted it.
    /// </summary>
    private RecentRequestEntry? FindEntry(string requestId)
    {
        if (_inFlight.TryGetValue(requestId, out var running))
        {
            return running;
        }

        foreach (var entry in _recentRequests)
        {
            if (string.Equals(entry.RequestId, requestId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private RecentRequestEntry MergeUsage(RecentRequestEntry entry) =>
        _usageByRequest.TryGetValue(entry.RequestId, out var usage) ? entry.WithUsage(usage) : entry;

    public void BeginInFlight(RecentRequestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The cap is checked against a separately maintained counter. ConcurrentDictionary.Count
        // acquires every internal lock of the dictionary to produce an exact answer, which made this
        // one line — executed once per forwarded request — a global barrier that stalled every
        // concurrent BeginInFlight/CompleteInFlight and grew with concurrency.
        if (Volatile.Read(ref _inFlightCount) >= MaxInFlightTracked && !_inFlight.ContainsKey(entry.RequestId))
        {
            return;
        }

        if (_inFlight.TryAdd(entry.RequestId, entry with { IsInFlight = true }))
        {
            Interlocked.Increment(ref _inFlightCount);
        }
        else
        {
            _inFlight[entry.RequestId] = entry with { IsInFlight = true };
        }

        Touch();
    }

    public void CompleteInFlight(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId) && _inFlight.TryRemove(requestId, out _))
        {
            Interlocked.Decrement(ref _inFlightCount);
            Touch();
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
            .Select(e => MergeUsage(e with { DurationMs = Math.Max(0, (now - e.TimestampUtc).TotalMilliseconds) }))
            .ToList();

        if (running.Count >= take)
        {
            return running;
        }

        running.AddRange(_recentRequests.Reverse().Take(take - running.Count).Select(MergeUsage));
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

    /// <summary>Lifetime count of requests whose client disconnected mid-response. Not part of <c>Errors</c>.</summary>
    public long GetClientDisconnects()
    {
        lock (_statsSync)
        {
            return _clientDisconnects;
        }
    }

    /// <summary>Records the current in-flight count into the windowed series and bumps the version.</summary>
    public void SampleInFlight()
    {
        Windows.SampleInFlight(GetActiveRequests());
        Touch();
    }

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
                ClientDisconnects = _clientDisconnects,
                TotalLatencyMs = _totalLatencyMs,
                RateLimitRejections = Interlocked.Read(ref _rateLimitRejections),
                QuotaRejections = Interlocked.Read(ref _quotaRejections),
                RequestsPerModel = new Dictionary<string, long>(_requestsPerModel, StringComparer.OrdinalIgnoreCase),
                ErrorsPerModel = new Dictionary<string, long>(_errorsPerModel, StringComparer.OrdinalIgnoreCase),
                // Pricing is merged in so a restart does not turn every restored row back into
                // "pending" with nothing left to settle it.
                Recent = _recentRequests.Select(MergeUsage).ToList(),
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
            _clientDisconnects = snapshot.ClientDisconnects;
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

            Touch();
        }
    }
}
