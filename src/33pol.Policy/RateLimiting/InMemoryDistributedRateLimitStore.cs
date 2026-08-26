using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class InMemoryDistributedRateLimitStore : IDistributedRateLimitStore
{
    private readonly ConcurrentDictionary<string, RequestWindowState> _requestWindows = new();
    private readonly ConcurrentDictionary<string, StreamConcurrencyState> _streamSlots = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _partitionRetention;
    private readonly TimeSpan _minCompactionInterval;
    private readonly int _compactEveryOperations;
    private readonly int _maxPartitions;
    private int _operationCount;
    private int _compacting;

    /// <summary>UTC ticks of the last sweep; zero until the first one runs.</summary>
    private long _lastCompactionTicks;

    public InMemoryDistributedRateLimitStore(
        IOptions<RateLimitingOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        var retentionSeconds = options?.Value.InMemoryPartitionRetentionSeconds ?? 3600;
        _partitionRetention = TimeSpan.FromSeconds(Math.Max(1, retentionSeconds));

        var compactEvery = options?.Value.InMemoryCompactionEveryOperations ?? 256;
        _compactEveryOperations = Math.Max(1, compactEvery);

        var minIntervalSeconds = options?.Value.InMemoryCompactionMinIntervalSeconds ?? 5;
        _minCompactionInterval = TimeSpan.FromSeconds(Math.Max(0, minIntervalSeconds));

        var maxPartitions = options?.Value.InMemoryMaxPartitions ?? 50_000;
        _maxPartitions = Math.Max(1, maxPartitions);
    }

    /// <summary>
    /// Admits a request against the partition's token bucket.
    /// </summary>
    /// <remarks>
    /// <para>The bucket holds up to <c>Rpm + Burst</c> tokens and refills continuously at
    /// <c>Rpm</c> tokens per minute. A full bucket therefore admits the same instantaneous burst the
    /// old per-minute window did, but the steady state is a smooth <c>Rpm</c>-per-minute drip rather
    /// than "everything at the top of the minute, nothing until the next".</para>
    ///
    /// <para>That distinction is what callers feel. A fixed window answered a rejected request with
    /// <c>Retry-After</c> of up to 59 seconds — the time to the next minute boundary — and every
    /// OpenAI-compatible SDK honours that header by sleeping for it, so a tenant that burst past its
    /// limit saw all further calls hang for most of a minute and read it as the gateway queueing
    /// them one by one. With a continuously refilling bucket the wait for the next token is
    /// <c>60 / Rpm</c> seconds, so <c>Retry-After</c> is almost always 1 second.</para>
    /// </remarks>
    public RateLimitAcquireResult TryAcquireRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now)
    {
        var capacity = policy.Rpm + policy.Burst;
        if (capacity <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        var state = GetOrAddWindow(partitionKey, now);

        // Refill, the limit decision and the debit happen under one lock, so a rejected request
        // never consumes a token and the decision is always made against the current fill.
        var reading = state.Apply(TokenOperation.TakeIfAvailable, now, capacity, RefillPerSecond(policy));

        // Compaction runs on both outcomes: a partition stuck in permanent rejection would otherwise
        // never be swept.
        CompactIfNeeded(now);

        return ToResult(reading, capacity, RefillPerSecond(policy));
    }

    /// <inheritdoc />
    public RateLimitAcquireResult PeekRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now)
    {
        var capacity = policy.Rpm + policy.Burst;
        if (capacity <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        // A peek must not create a partition: callers peek on every request but debit only on the
        // ones that fail, so materialising a bucket here would fill the table with entries that are
        // never charged anything.
        if (!_requestWindows.TryGetValue(partitionKey, out var state))
        {
            return new RateLimitAcquireResult(true, Limit: capacity, Remaining: capacity, ResetAfterSeconds: 0);
        }

        var reading = state.Apply(TokenOperation.Peek, now, capacity, RefillPerSecond(policy));
        return ToResult(reading, capacity, RefillPerSecond(policy));
    }

    /// <inheritdoc />
    public void DebitRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now)
    {
        var capacity = policy.Rpm + policy.Burst;
        if (capacity <= 0)
        {
            return;
        }

        var state = GetOrAddWindow(partitionKey, now);
        state.Apply(TokenOperation.ForceTake, now, capacity, RefillPerSecond(policy));
        CompactIfNeeded(now);
    }

    public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy)
    {
        if (policy.MaxConcurrentStreams <= 0)
        {
            // Zero is "unlimited", not "denied" — see RateLimitPolicy.
            return new RateLimitAcquireResult(true);
        }

        var now = _timeProvider.GetUtcNow();

        // Eviction marks a state as tombstoned and then removes it from the dictionary. In the
        // window between those two steps GetOrAdd can hand back the tombstone, whose TryAcquire
        // always fails — producing a spurious 429 for a tenant with zero active streams. Retry
        // until a live state is obtained; the tombstoned entry is on its way out.
        StreamConcurrencyState state;
        bool acquired;
        int active;
        while (true)
        {
            state = _streamSlots.GetOrAdd(partitionKey, _ => new StreamConcurrencyState(now));
            if (state.TryAcquire(policy.MaxConcurrentStreams, now, out acquired, out active))
            {
                break;
            }

            // Tombstoned: help it out of the dictionary so the next GetOrAdd creates a fresh state.
            _streamSlots.TryRemove(new KeyValuePair<string, StreamConcurrencyState>(partitionKey, state));
        }

        // On both outcomes, for the same reason the request path does it: a partition parked at its
        // stream cap would otherwise never drive a sweep.
        CompactIfNeeded(now);

        var remaining = Math.Max(0, policy.MaxConcurrentStreams - active);
        if (!acquired)
        {
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.ConcurrencyLimitExceeded,
                RetryAfterSeconds: 1,
                Limit: policy.MaxConcurrentStreams,
                Remaining: remaining);
        }

        return new RateLimitAcquireResult(
            true,
            Limit: policy.MaxConcurrentStreams,
            Remaining: remaining);
    }

    public void ReleaseStreamSlot(string partitionKey)
    {
        if (_streamSlots.TryGetValue(partitionKey, out var state))
        {
            state.Release(_timeProvider.GetUtcNow());
        }
    }

    private static double RefillPerSecond(RateLimitPolicy policy) => Math.Max(1, policy.Rpm) / 60.0;

    private RequestWindowState GetOrAddWindow(string partitionKey, DateTimeOffset now) =>
        _requestWindows.GetOrAdd(partitionKey, _ => new RequestWindowState(now));

    private static RateLimitAcquireResult ToResult(BucketReading reading, int capacity, double refillPerSecond)
    {
        var remaining = (int)Math.Floor(Math.Max(0, reading.Tokens));
        var resetAfter = (int)Math.Ceiling(Math.Max(0, capacity - reading.Tokens) / refillPerSecond);

        if (reading.HasToken)
        {
            return new RateLimitAcquireResult(
                true,
                Limit: capacity,
                Remaining: remaining,
                ResetAfterSeconds: resetAfter);
        }

        return new RateLimitAcquireResult(
            false,
            GatewayRateLimitReason.RateLimitExceeded,
            Math.Max(1, (int)Math.Ceiling(reading.RetryAfter.TotalSeconds)),
            Limit: capacity,
            Remaining: remaining,
            ResetAfterSeconds: resetAfter);
    }

    /// <summary>
    /// Sweeps partitions nobody has touched for the retention window, and enforces the partition
    /// ceiling.
    /// </summary>
    /// <remarks>
    /// A sweep is O(live partitions) and runs inline on whichever request triggered it, so it is
    /// gated three ways: an operation counter, a minimum wall-clock interval, and a single-sweeper
    /// latch so a burst of concurrent requests cannot all scan at once. Passing the partition
    /// ceiling overrides the first two — that is the case where deferring the work is what lets the
    /// table grow.
    /// </remarks>
    private void CompactIfNeeded(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _operationCount) % _compactEveryOperations != 0)
        {
            return;
        }

        // Counted first: ConcurrentDictionary.Count takes every bucket lock, so it belongs behind
        // the operation counter rather than on every acquire. Between two counts the table can grow
        // by at most one interval's worth of new partitions, which the ceiling has room for.
        var overCapacity = _requestWindows.Count > _maxPartitions || _streamSlots.Count > _maxPartitions;
        if (!overCapacity && _minCompactionInterval > TimeSpan.Zero)
        {
            // Both sides are UTC tick counts, so this cannot overflow; the zero it starts at is
            // "never swept", which is always further back than any interval.
            var since = now.UtcTicks - Interlocked.Read(ref _lastCompactionTicks);
            if (since >= 0 && since < _minCompactionInterval.Ticks)
            {
                return;
            }
        }

        // One sweeper at a time. A second thread arriving mid-sweep has nothing to add: the sweep
        // already in flight covers the same partitions.
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastCompactionTicks, now.UtcTicks);
            Compact(now);
        }
        finally
        {
            Interlocked.Exchange(ref _compacting, 0);
        }
    }

    private void Compact(DateTimeOffset now)
    {
        var staleThreshold = now - _partitionRetention;

        foreach (var pair in _requestWindows)
        {
            if (pair.Value.IsStale(staleThreshold))
            {
                // Value-matched removal: between the staleness check and the removal another thread
                // can replace this entry with a freshly created bucket. Dropping that one instead
                // would hand its partition a full bucket it did not earn.
                _requestWindows.TryRemove(new KeyValuePair<string, RequestWindowState>(pair.Key, pair.Value));
            }
        }

        foreach (var pair in _streamSlots)
        {
            if (pair.Value.TryMarkEvicted(staleThreshold))
            {
                _streamSlots.TryRemove(new KeyValuePair<string, StreamConcurrencyState>(pair.Key, pair.Value));
            }
        }

        EnforceCeiling(_requestWindows, static state => state.LastSeenUtc, static _ => true);
        EnforceCeiling(_streamSlots, static state => state.LastSeenUtc, static state => state.TryMarkEvicted(DateTimeOffset.MaxValue));
    }

    /// <summary>
    /// Evicts least-recently-seen partitions until the dictionary is back under the ceiling.
    /// </summary>
    /// <remarks>
    /// Ordering by last-seen is what makes this safe to do at all: a partition that is actively
    /// being rejected is touched on every rejection, so it sorts to the end and cannot be evicted
    /// (and thereby handed a fresh bucket) by a caller flooding the table with new partitions.
    /// </remarks>
    private void EnforceCeiling<TState>(
        ConcurrentDictionary<string, TState> partitions,
        Func<TState, DateTimeOffset> lastSeen,
        Func<TState, bool> canEvict)
        where TState : class
    {
        var excess = partitions.Count - _maxPartitions;
        if (excess <= 0)
        {
            return;
        }

        var victims = partitions
            .ToArray()
            .OrderBy(pair => lastSeen(pair.Value))
            .Take(excess);

        foreach (var victim in victims)
        {
            if (canEvict(victim.Value))
            {
                partitions.TryRemove(victim);
            }
        }
    }

    private enum TokenOperation
    {
        /// <summary>Take one token if the bucket holds one; report whether it did.</summary>
        TakeIfAvailable,

        /// <summary>Report the fill without changing it.</summary>
        Peek,

        /// <summary>Take one token whether or not the bucket holds one, flooring at empty.</summary>
        ForceTake,
    }

    /// <summary>The bucket's state after an operation, in the terms every caller needs.</summary>
    private readonly record struct BucketReading(bool HasToken, double Tokens, TimeSpan RetryAfter);

    /// <summary>Token bucket for one partition. All members are guarded by <see cref="_sync"/>.</summary>
    private sealed class RequestWindowState
    {
        private readonly object _sync = new();
        private double _tokens;
        private DateTimeOffset _lastRefillUtc;
        private DateTimeOffset _lastSeenUtc;
        private bool _primed;

        public RequestWindowState(DateTimeOffset now)
        {
            _lastRefillUtc = now;
            _lastSeenUtc = now;
        }

        public DateTimeOffset LastSeenUtc
        {
            get
            {
                lock (_sync)
                {
                    return _lastSeenUtc;
                }
            }
        }

        /// <summary>
        /// Refills the bucket for the time elapsed since the last call, then applies
        /// <paramref name="operation"/>. When no token is available,
        /// <see cref="BucketReading.RetryAfter"/> is how long until the next one arrives at the
        /// current refill rate.
        /// </summary>
        public BucketReading Apply(
            TokenOperation operation,
            DateTimeOffset now,
            int capacity,
            double refillPerSecond)
        {
            lock (_sync)
            {
                if (!_primed)
                {
                    // A new (or evicted-and-recreated) partition starts full: its first burst is
                    // admitted exactly as a fresh minute window would have admitted it.
                    _tokens = capacity;
                    _primed = true;
                }

                // The refill anchor only ever moves forwards. Time can arrive out of order here for
                // two reasons: the wall clock stepped back, or — far more often — two concurrent
                // requests read the clock in one order and reached this lock in the other. Winding
                // the anchor back to the earlier reading is what would grant a windfall: the span
                // between the two readings then refills a second time on the next call. Refusing to
                // move it means a backwards step pauses refilling until the clock catches up, which
                // errs towards limiting too much rather than too little.
                var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    _tokens = Math.Min(capacity, _tokens + (elapsedSeconds * refillPerSecond));
                    _lastRefillUtc = now;
                }

                // Touch on every attempt, including rejections, so an actively-rejected partition is
                // not treated as stale and evicted from under itself.
                if (now > _lastSeenUtc)
                {
                    _lastSeenUtc = now;
                }

                var hasToken = _tokens >= 1.0;
                if (hasToken && operation != TokenOperation.Peek)
                {
                    _tokens -= 1.0;
                }
                else if (!hasToken && operation == TokenOperation.ForceTake)
                {
                    // The caller already committed to the cost; an empty bucket floors at zero
                    // rather than going negative, which would take several windows to work off.
                    _tokens = 0;
                }

                var retryAfter = hasToken
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds((1.0 - _tokens) / refillPerSecond);

                return new BucketReading(hasToken, _tokens, retryAfter);
            }
        }

        public bool IsStale(DateTimeOffset staleThreshold)
        {
            lock (_sync)
            {
                return _lastSeenUtc < staleThreshold;
            }
        }
    }

    private sealed class StreamConcurrencyState
    {
        private readonly object _sync = new();
        private int _active;
        private DateTimeOffset _lastSeenUtc;
        private bool _evicted;

        public StreamConcurrencyState(DateTimeOffset now)
        {
            _lastSeenUtc = now;
        }

        public DateTimeOffset LastSeenUtc
        {
            get
            {
                lock (_sync)
                {
                    return _lastSeenUtc;
                }
            }
        }

        /// <summary>
        /// Attempts to take a slot. Returns false when this state has been tombstoned by compaction,
        /// which the caller must distinguish from a genuine limit rejection (reported via
        /// <paramref name="acquired"/>) — conflating the two is what produced spurious 429s.
        /// </summary>
        /// <param name="active">Slots held after the attempt, for the caller's "remaining" report.</param>
        public bool TryAcquire(int maxConcurrent, DateTimeOffset now, out bool acquired, out int active)
        {
            lock (_sync)
            {
                if (_evicted)
                {
                    acquired = false;
                    active = 0;
                    return false;
                }

                if (_active >= maxConcurrent)
                {
                    acquired = false;
                    active = _active;
                    return true;
                }

                _active++;
                if (now > _lastSeenUtc)
                {
                    _lastSeenUtc = now;
                }

                acquired = true;
                active = _active;
                return true;
            }
        }

        public void Release(DateTimeOffset now)
        {
            lock (_sync)
            {
                _active = Math.Max(0, _active - 1);
                if (now > _lastSeenUtc)
                {
                    _lastSeenUtc = now;
                }
            }
        }

        /// <summary>
        /// Tombstones an idle state so it can be removed. A state holding slots is never evicted, at
        /// any threshold — <see cref="DateTimeOffset.MaxValue"/> forces eviction of idle states only.
        /// </summary>
        public bool TryMarkEvicted(DateTimeOffset staleThreshold)
        {
            lock (_sync)
            {
                if (_evicted || _active > 0 || _lastSeenUtc >= staleThreshold)
                {
                    return false;
                }

                _evicted = true;
                return true;
            }
        }
    }
}
