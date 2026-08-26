using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

/// <summary>
/// The counters behind every admission decision: one token bucket and one concurrency-slot state per
/// partition, held in memory for the life of the process.
/// </summary>
/// <remarks>
/// <para>In-memory is the right storage here and not a placeholder for something distributed. The
/// gateway is a single process writing to one embedded database, so there is no second instance to
/// coordinate with; a network round trip per admission decision would add more latency than the
/// decision saves. The <c>IDistributedRateLimitStore</c> name is the seam a Redis-backed
/// implementation would fill if the gateway ever scales out — the interface is deliberately shaped
/// so every operation is one round trip and none of them require a read-modify-write from the
/// caller.</para>
///
/// <para>Maintenance — sweeping stale partitions and holding the table under its ceiling — is driven
/// by <see cref="RateLimitMaintenanceHostedService"/> on a timer, not from the request path. It used
/// to run inline on whichever request tripped an operation counter, which made one request in every
/// few hundred pay an O(live partitions) scan, and past the ceiling a full array copy and sort of up
/// to 50,000 entries. That is invisible in a mean and very visible at p99.</para>
/// </remarks>
public sealed class InMemoryDistributedRateLimitStore : IDistributedRateLimitStore
{
    private readonly ConcurrentDictionary<string, RequestWindowState> _requestWindows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamConcurrencyState> _streamSlots = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _partitionRetention;
    private readonly int _maxPartitions;

    // Maintained with the dictionaries rather than read from them: ConcurrentDictionary.Count takes
    // every bucket lock, which is not something the ceiling check should cost when it runs on a
    // timer over a table this size.
    private int _requestPartitionCount;
    private int _streamPartitionCount;

    public InMemoryDistributedRateLimitStore(
        IOptions<RateLimitingOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        var retentionSeconds = options?.Value.InMemoryPartitionRetentionSeconds ?? 3600;
        _partitionRetention = TimeSpan.FromSeconds(Math.Max(1, retentionSeconds));

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
        var capacity = policy.Capacity;
        if (capacity <= 0)
        {
            return RateLimitAcquireResult.Unlimited;
        }

        var refill = RefillPerSecond(policy);
        var state = GetOrAddWindow(partitionKey, now);

        // Refill, the limit decision and the debit happen under one lock, so a rejected request
        // never consumes a token and the decision is always made against the current fill.
        var reading = state.Apply(TokenOperation.TakeIfAvailable, now, capacity, refill);
        return ToResult(
            reading,
            capacity,
            refill,
            scope: null,
            partitionKey,
            adaptiveFactor: 1.0,
            policy.Rpm,
            policy.Rpm);
    }

    /// <inheritdoc />
    public RateLimitAcquireResult TryAcquireAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now)
    {
        // The tightest admitting rule, tracked as we go: the client is told about the limit closest
        // to refusing it, because reporting the roomiest one would have it pace against a budget
        // that is not the one about to run out.
        RateLimitAcquireResult? tightest = null;
        var tightestRatio = double.MaxValue;
        var taken = 0;

        for (var i = 0; i < rules.Length; i++)
        {
            ref readonly var rule = ref rules[i];
            var capacity = rule.Policy.Capacity;
            if (capacity <= 0)
            {
                continue;
            }

            var refill = RefillPerSecond(rule.Policy);
            var state = GetOrAddWindow(rule.PartitionKey, now);
            var reading = state.Apply(TokenOperation.TakeIfAvailable, now, capacity, refill);

            if (!reading.HasToken)
            {
                // Hand back every token taken by the rules before this one. Without it a caller
                // pinned by its narrowest limit would still spend its tenant-wide and gateway-wide
                // budget on each attempt, so one over-limit key would rate-limit its whole tenant.
                RefundRange(rules[..i], now);
                return ToResult(
                    reading,
                    capacity,
                    refill,
                    rule.Scope,
                    rule.PartitionKey,
                    rule.AdaptiveFactor,
                    rule.ConfiguredRpm,
                    rule.Policy.Rpm);
            }

            taken++;

            var ratio = reading.Tokens / capacity;
            if (ratio < tightestRatio)
            {
                tightestRatio = ratio;
                tightest = ToResult(
                    reading,
                    capacity,
                    refill,
                    rule.Scope,
                    rule.PartitionKey,
                    rule.AdaptiveFactor,
                    rule.ConfiguredRpm,
                    rule.Policy.Rpm);
            }
        }

        return taken == 0 ? RateLimitAcquireResult.Unlimited : tightest!;
    }

    /// <inheritdoc />
    public void RefundAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now) => RefundRange(rules, now);

    private void RefundRange(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now)
    {
        foreach (ref readonly var rule in rules)
        {
            var capacity = rule.Policy.Capacity;
            if (capacity <= 0)
            {
                continue;
            }

            // TryGetValue, not GetOrAdd: a partition that has been swept between the take and the
            // refund starts full again anyway, and materialising it here would only add an entry
            // nobody is counting against.
            if (_requestWindows.TryGetValue(rule.PartitionKey, out var state))
            {
                state.Apply(TokenOperation.Refund, now, capacity, RefillPerSecond(rule.Policy));
            }
        }
    }

    /// <inheritdoc />
    public RateLimitAcquireResult PeekRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now)
    {
        var capacity = policy.Capacity;
        if (capacity <= 0)
        {
            return RateLimitAcquireResult.Unlimited;
        }

        var refill = RefillPerSecond(policy);

        // A peek must not create a partition: callers peek on every request but debit only on the
        // ones that fail, so materialising a bucket here would fill the table with entries that are
        // never charged anything.
        if (!_requestWindows.TryGetValue(partitionKey, out var state))
        {
            return new RateLimitAcquireResult(
                true,
                Limit: capacity,
                Remaining: capacity,
                ResetAfterSeconds: 0,
                PartitionKey: partitionKey,
                ConfiguredRpm: policy.Rpm,
                EffectiveRpm: policy.Rpm);
        }

        var reading = state.Apply(TokenOperation.Peek, now, capacity, refill);
        return ToResult(
            reading,
            capacity,
            refill,
            scope: null,
            partitionKey,
            adaptiveFactor: 1.0,
            policy.Rpm,
            policy.Rpm);
    }

    /// <inheritdoc />
    public void DebitRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now)
    {
        var capacity = policy.Capacity;
        if (capacity <= 0)
        {
            return;
        }

        var state = GetOrAddWindow(partitionKey, now);
        state.Apply(TokenOperation.ForceTake, now, capacity, RefillPerSecond(policy));
    }

    public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy)
    {
        if (!policy.EnforcesConcurrency)
        {
            // Zero is "unlimited", not "denied" — see RateLimitPolicy.
            return RateLimitAcquireResult.Unlimited;
        }

        var now = _timeProvider.GetUtcNow();
        var outcome = AcquireSlot(partitionKey, policy.MaxConcurrentStreams, now);
        return ToSlotResult(outcome, policy.MaxConcurrentStreams, scope: null, partitionKey);
    }

    /// <inheritdoc />
    public RateLimitAcquireResult TryAcquireStreamSlots(
        ReadOnlySpan<RateLimitRule> rules,
        out RateLimitSlotLease held)
    {
        held = RateLimitSlotLease.Empty;

        var capping = 0;
        foreach (ref readonly var rule in rules)
        {
            if (rule.Policy.EnforcesConcurrency)
            {
                capping++;
            }
        }

        if (capping == 0)
        {
            return RateLimitAcquireResult.Unlimited;
        }

        var now = _timeProvider.GetUtcNow();
        var keys = new string[capping];
        var taken = 0;

        // The tightest cap, for the same reason the rate path tracks one: a streaming client should
        // be told how many slots are left in the scope nearest to full.
        RateLimitAcquireResult? tightest = null;
        var tightestRemaining = int.MaxValue;

        foreach (ref readonly var rule in rules)
        {
            if (!rule.Policy.EnforcesConcurrency)
            {
                continue;
            }

            var outcome = AcquireSlot(rule.PartitionKey, rule.Policy.MaxConcurrentStreams, now);
            if (!outcome.Acquired)
            {
                // Give back the slots already taken. A streaming response holds its slots for its
                // whole lifetime, so a leak here is not a transient overcount — it permanently
                // shrinks the scope's capacity until the process restarts.
                ReleaseSlots(keys.AsSpan(0, taken));
                return ToSlotResult(outcome, rule.Policy.MaxConcurrentStreams, rule.Scope, rule.PartitionKey);
            }

            keys[taken++] = rule.PartitionKey;

            var remaining = Math.Max(0, rule.Policy.MaxConcurrentStreams - outcome.Active);
            if (remaining < tightestRemaining)
            {
                tightestRemaining = remaining;
                tightest = ToSlotResult(outcome, rule.Policy.MaxConcurrentStreams, rule.Scope, rule.PartitionKey);
            }
        }

        held = new RateLimitSlotLease(keys, taken);
        return tightest!;
    }

    public void ReleaseStreamSlot(string partitionKey)
    {
        if (_streamSlots.TryGetValue(partitionKey, out var state))
        {
            state.Release(_timeProvider.GetUtcNow());
        }
    }

    /// <inheritdoc />
    public void ReleaseStreamSlots(RateLimitSlotLease held) => ReleaseSlots(held.Keys);

    /// <inheritdoc />
    public RateLimitStoreStats GetStats() => new(
        Volatile.Read(ref _requestPartitionCount),
        Volatile.Read(ref _streamPartitionCount),
        _maxPartitions);

    private void ReleaseSlots(ReadOnlySpan<string> keys)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var key in keys)
        {
            if (_streamSlots.TryGetValue(key, out var state))
            {
                state.Release(now);
            }
        }
    }

    private SlotOutcome AcquireSlot(string partitionKey, int maxConcurrent, DateTimeOffset now)
    {
        // Eviction marks a state as tombstoned and then removes it from the dictionary. In the
        // window between those two steps GetOrAdd can hand back the tombstone, whose TryAcquire
        // always fails — producing a spurious 429 for a tenant with zero active streams. Retry
        // until a live state is obtained; the tombstoned entry is on its way out.
        while (true)
        {
            var state = GetOrAddSlotState(partitionKey, now);
            if (state.TryAcquire(maxConcurrent, now, out var acquired, out var active))
            {
                return new SlotOutcome(acquired, active);
            }

            if (_streamSlots.TryRemove(new KeyValuePair<string, StreamConcurrencyState>(partitionKey, state)))
            {
                Interlocked.Decrement(ref _streamPartitionCount);
            }
        }
    }

    private static double RefillPerSecond(RateLimitPolicy policy) => Math.Max(1, policy.Rpm) / 60.0;

    private RequestWindowState GetOrAddWindow(string partitionKey, DateTimeOffset now)
    {
        // The hit path is a single lookup with no allocation; only a genuinely new partition pays
        // for a state object, and only the thread that installed it adjusts the count.
        if (_requestWindows.TryGetValue(partitionKey, out var existing))
        {
            return existing;
        }

        var created = new RequestWindowState(now);
        var state = _requestWindows.GetOrAdd(partitionKey, created);
        if (ReferenceEquals(state, created))
        {
            Interlocked.Increment(ref _requestPartitionCount);
        }

        return state;
    }

    private StreamConcurrencyState GetOrAddSlotState(string partitionKey, DateTimeOffset now)
    {
        if (_streamSlots.TryGetValue(partitionKey, out var existing))
        {
            return existing;
        }

        var created = new StreamConcurrencyState(now);
        var state = _streamSlots.GetOrAdd(partitionKey, created);
        if (ReferenceEquals(state, created))
        {
            Interlocked.Increment(ref _streamPartitionCount);
        }

        return state;
    }

    /// <summary>
    /// Turns a bucket reading into the result every caller sees.
    /// </summary>
    /// <param name="configuredRpm">The operator-configured sustained rate, before adaptation.</param>
    /// <param name="effectiveRpm">
    /// The sustained rate enforced. Carried separately from <paramref name="capacity"/> because the
    /// two answer different questions: capacity is the budget a client's remaining count is measured
    /// against, the rate is what a usage report compares an observed rate to. Reporting capacity as
    /// the rate understates utilisation by the entire burst allowance.
    /// </param>
    private static RateLimitAcquireResult ToResult(
        BucketReading reading,
        int capacity,
        double refillPerSecond,
        RateLimitScope? scope,
        string partitionKey,
        double adaptiveFactor,
        int configuredRpm,
        int effectiveRpm)
    {
        var remaining = (int)Math.Floor(Math.Max(0, reading.Tokens));
        var resetAfter = (int)Math.Ceiling(Math.Max(0, capacity - reading.Tokens) / refillPerSecond);

        if (reading.HasToken)
        {
            return new RateLimitAcquireResult(
                true,
                Limit: capacity,
                Remaining: remaining,
                ResetAfterSeconds: resetAfter,
                Scope: scope,
                PartitionKey: partitionKey,
                AdaptiveFactor: adaptiveFactor,
                ConfiguredRpm: configuredRpm,
                EffectiveRpm: effectiveRpm);
        }

        return new RateLimitAcquireResult(
            false,
            GatewayRateLimitReason.RateLimitExceeded,
            Math.Max(1, (int)Math.Ceiling(reading.RetryAfter.TotalSeconds)),
            Limit: capacity,
            Remaining: remaining,
            ResetAfterSeconds: resetAfter,
            Scope: scope,
            PartitionKey: partitionKey,
            AdaptiveFactor: adaptiveFactor,
            ConfiguredRpm: configuredRpm,
            EffectiveRpm: effectiveRpm);
    }

    private static RateLimitAcquireResult ToSlotResult(
        SlotOutcome outcome,
        int maxConcurrent,
        RateLimitScope? scope,
        string partitionKey)
    {
        var remaining = Math.Max(0, maxConcurrent - outcome.Active);
        if (!outcome.Acquired)
        {
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.ConcurrencyLimitExceeded,
                RetryAfterSeconds: 1,
                Limit: maxConcurrent,
                Remaining: remaining,
                Scope: scope,
                PartitionKey: partitionKey);
        }

        return new RateLimitAcquireResult(
            true,
            Limit: maxConcurrent,
            Remaining: remaining,
            Scope: scope,
            PartitionKey: partitionKey);
    }

    /// <inheritdoc />
    public int Compact(DateTimeOffset now)
    {
        var staleThreshold = now - _partitionRetention;
        var removed = 0;

        foreach (var pair in _requestWindows)
        {
            if (pair.Value.IsStale(staleThreshold) &&
                // Value-matched removal: between the staleness check and the removal another thread
                // can replace this entry with a freshly created bucket. Dropping that one instead
                // would hand its partition a full bucket it did not earn.
                _requestWindows.TryRemove(new KeyValuePair<string, RequestWindowState>(pair.Key, pair.Value)))
            {
                Interlocked.Decrement(ref _requestPartitionCount);
                removed++;
            }
        }

        foreach (var pair in _streamSlots)
        {
            if (pair.Value.TryMarkEvicted(staleThreshold) &&
                _streamSlots.TryRemove(new KeyValuePair<string, StreamConcurrencyState>(pair.Key, pair.Value)))
            {
                Interlocked.Decrement(ref _streamPartitionCount);
                removed++;
            }
        }

        removed += EnforceCeiling(
            _requestWindows,
            ref _requestPartitionCount,
            static state => state.LastSeenUtc,
            static _ => true);
        removed += EnforceCeiling(
            _streamSlots,
            ref _streamPartitionCount,
            static state => state.LastSeenUtc,
            static state => state.TryMarkEvicted(DateTimeOffset.MaxValue));

        return removed;
    }

    /// <summary>
    /// Evicts least-recently-seen partitions until the dictionary is back under the ceiling.
    /// </summary>
    /// <remarks>
    /// Ordering by last-seen is what makes this safe to do at all: a partition that is actively
    /// being rejected is touched on every rejection, so it sorts to the end and cannot be evicted
    /// (and thereby handed a fresh bucket) by a caller flooding the table with new partitions.
    /// </remarks>
    private int EnforceCeiling<TState>(
        ConcurrentDictionary<string, TState> partitions,
        ref int liveCount,
        Func<TState, DateTimeOffset> lastSeen,
        Func<TState, bool> canEvict)
        where TState : class
    {
        var excess = Volatile.Read(ref liveCount) - _maxPartitions;
        if (excess <= 0)
        {
            return 0;
        }

        var victims = partitions
            .ToArray()
            .OrderBy(pair => lastSeen(pair.Value))
            .Take(excess);

        var removed = 0;
        foreach (var victim in victims)
        {
            if (canEvict(victim.Value) && partitions.TryRemove(victim))
            {
                Interlocked.Decrement(ref liveCount);
                removed++;
            }
        }

        return removed;
    }

    private readonly record struct SlotOutcome(bool Acquired, int Active);

    private enum TokenOperation
    {
        /// <summary>Take one token if the bucket holds one; report whether it did.</summary>
        TakeIfAvailable,

        /// <summary>Report the fill without changing it.</summary>
        Peek,

        /// <summary>Take one token whether or not the bucket holds one, flooring at empty.</summary>
        ForceTake,

        /// <summary>Give one token back, for a request another scope went on to refuse.</summary>
        Refund,
    }

    /// <summary>The bucket's state after an operation, in the terms every caller needs.</summary>
    private readonly record struct BucketReading(bool HasToken, double Tokens, TimeSpan RetryAfter);

    /// <summary>Token bucket for one partition. All members are guarded by <see cref="_sync"/>.</summary>
    private sealed class RequestWindowState
    {
        private readonly object _sync = new();
        private double _tokens;
        private DateTimeOffset _lastRefillUtc;
        private bool _primed;

        // Read without the lock by the maintenance sweep, which sorts up to the whole table by it.
        // Taking the per-partition lock 50,000 times to order a list is a cost with no correctness
        // to show for it: a last-seen that is one operation stale only changes which of two
        // equally-idle partitions is evicted first.
        private long _lastSeenTicks;

        public RequestWindowState(DateTimeOffset now)
        {
            _lastRefillUtc = now;
            _lastSeenTicks = now.UtcTicks;
        }

        public DateTimeOffset LastSeenUtc => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

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
                TouchLastSeen(now);

                if (operation == TokenOperation.Refund)
                {
                    // Capped at capacity, so a refund that races a sweep — or an unmatched one from
                    // any future caller — can never inflate a partition past its tier.
                    _tokens = Math.Min(capacity, _tokens + 1.0);
                    return new BucketReading(true, _tokens, TimeSpan.Zero);
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

        public bool IsStale(DateTimeOffset staleThreshold) => LastSeenUtc < staleThreshold;

        private void TouchLastSeen(DateTimeOffset now)
        {
            var ticks = now.UtcTicks;
            var current = Interlocked.Read(ref _lastSeenTicks);
            if (ticks > current)
            {
                Interlocked.Exchange(ref _lastSeenTicks, ticks);
            }
        }
    }

    private sealed class StreamConcurrencyState
    {
        private readonly object _sync = new();
        private int _active;
        private long _lastSeenTicks;
        private bool _evicted;

        public StreamConcurrencyState(DateTimeOffset now)
        {
            _lastSeenTicks = now.UtcTicks;
        }

        public DateTimeOffset LastSeenUtc => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

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
                    Touch(now);
                    return true;
                }

                _active++;
                Touch(now);

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
                Touch(now);
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
                if (_evicted || _active > 0 || LastSeenUtc >= staleThreshold)
                {
                    return false;
                }

                _evicted = true;
                return true;
            }
        }

        private void Touch(DateTimeOffset now)
        {
            var ticks = now.UtcTicks;
            if (ticks > Interlocked.Read(ref _lastSeenTicks))
            {
                Interlocked.Exchange(ref _lastSeenTicks, ticks);
            }
        }
    }
}
