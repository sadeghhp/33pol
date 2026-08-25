using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Proxy.Resilience;

/// <summary>
/// Per-model concurrency bulkhead with an optional bounded wait queue.
/// </summary>
/// <remarks>
/// <para>Admission is two-tiered. Up to <c>MaxConcurrentForwardsPerModel</c> requests are forwarded
/// at once; when every slot is taken, up to <c>MaxQueuedForwardsPerModel</c> further arrivals wait
/// (each for at most <c>BulkheadQueueTimeoutSeconds</c>) for a slot to free; anything beyond both
/// bounds — or a waiter whose patience runs out — is refused immediately so the gateway sheds load
/// instead of building an unbounded backlog.</para>
///
/// <para>Waiters are served in FIFO order by <see cref="SemaphoreSlim"/>, so a request that has
/// queued cannot be starved by later arrivals. Because the queue is per model, one model's backlog
/// never blocks another's; and because a waiter's cancellation token is the client's own abort, a
/// caller that gives up leaves the queue at once rather than holding a place it will never use.</para>
/// </remarks>
public sealed class BulkheadRegistry : IBulkheadStateSource
{
    private readonly ConcurrentDictionary<string, ModelBulkhead> _bulkheads = new(StringComparer.Ordinal);
    private readonly int _maxConcurrent;
    private readonly int _maxQueued;
    private readonly TimeSpan _queueTimeout;
    private readonly int _maxTrackedModels;
    private readonly IGatewayMetricsCollector _metrics;

    public BulkheadRegistry(IOptions<GatewayOptions> options, IGatewayMetricsCollector metrics)
    {
        var resilience = options.Value.Resilience;
        _maxConcurrent = Math.Max(1, resilience.MaxConcurrentForwardsPerModel);
        _maxQueued = Math.Max(0, resilience.MaxQueuedForwardsPerModel);
        _queueTimeout = TimeSpan.FromSeconds(Math.Max(1, resilience.BulkheadQueueTimeoutSeconds));
        _maxTrackedModels = resilience.MaxTrackedResilienceModels;
        _metrics = metrics;
    }

    /// <summary>Configured in-flight ceiling per model.</summary>
    public int MaxConcurrentPerModel => _maxConcurrent;

    /// <summary>Configured queue depth per model (0 = refuse at capacity).</summary>
    public int MaxQueuedPerModel => _maxQueued;

    /// <summary>Occupancy of every model bulkhead that has ever admitted a request. Models at zero are included so a quiet model still shows its ceiling.</summary>
    public IReadOnlyList<BulkheadModelState> GetStates()
    {
        var list = new List<BulkheadModelState>(_bulkheads.Count);
        foreach (var pair in _bulkheads)
        {
            var inFlight = Math.Max(0, _maxConcurrent - pair.Value.Semaphore.CurrentCount);
            list.Add(new BulkheadModelState(pair.Key, inFlight, pair.Value.QueuedCount, _maxConcurrent, _maxQueued));
        }

        return list;
    }

    /// <summary>
    /// Acquires a forwarding slot for <paramref name="modelId"/>, waiting in the bounded queue if
    /// the bulkhead is full. Returns null when the request must be refused: the queue is full, the
    /// wait timed out, or the model table is exhausted.
    /// </summary>
    public async ValueTask<IDisposable?> TryAcquireAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!_bulkheads.TryGetValue(modelId, out var bulkhead))
        {
            if (_bulkheads.Count >= _maxTrackedModels)
            {
                _metrics.RecordBulkheadRejection(modelId);
                return null;
            }

            bulkhead = _bulkheads.GetOrAdd(
                modelId,
                static (_, max) => new ModelBulkhead(max),
                _maxConcurrent);
        }

        // Fast path: a slot is free right now, no queueing bookkeeping needed.
        if (bulkhead.Semaphore.Wait(0, CancellationToken.None))
        {
            return Admit(bulkhead, modelId);
        }

        if (_maxQueued <= 0)
        {
            _metrics.RecordBulkheadRejection(modelId);
            return null;
        }

        // Claim a queue place atomically; the queue is bounded so a flood cannot pile up behind a
        // slow model indefinitely.
        if (!bulkhead.TryEnterQueue(_maxQueued))
        {
            _metrics.RecordBulkheadRejection(modelId);
            return null;
        }

        _metrics.RecordBulkheadQueuedChange(modelId, 1);
        try
        {
            if (!await bulkhead.Semaphore.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false))
            {
                _metrics.RecordBulkheadRejection(modelId);
                return null;
            }
        }
        finally
        {
            bulkhead.LeaveQueue();
            _metrics.RecordBulkheadQueuedChange(modelId, -1);
        }

        return Admit(bulkhead, modelId);
    }

    private IDisposable Admit(ModelBulkhead bulkhead, string modelId)
    {
        _metrics.RecordBulkheadInflightChange(modelId, 1);
        return new ReleaseHandle(bulkhead.Semaphore, modelId, _metrics);
    }

    private sealed class ModelBulkhead(int maxConcurrent)
    {
        private int _queued;

        public SemaphoreSlim Semaphore { get; } = new(maxConcurrent, maxConcurrent);

        public int QueuedCount => Math.Max(0, Volatile.Read(ref _queued));

        public bool TryEnterQueue(int maxQueued)
        {
            // Optimistic increment with rollback keeps the check-and-claim atomic without a lock.
            if (Interlocked.Increment(ref _queued) <= maxQueued)
            {
                return true;
            }

            Interlocked.Decrement(ref _queued);
            return false;
        }

        public void LeaveQueue() => Interlocked.Decrement(ref _queued);
    }

    private sealed class ReleaseHandle(
        SemaphoreSlim semaphore,
        string modelId,
        IGatewayMetricsCollector metrics) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
                metrics.RecordBulkheadInflightChange(modelId, -1);
            }
        }
    }
}
