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
    private readonly TimeSpan _partitionRetention;
    private readonly int _compactEveryOperations;
    private int _operationCount;

    public InMemoryDistributedRateLimitStore(IOptions<RateLimitingOptions>? options = null)
    {
        var retentionSeconds = options?.Value.InMemoryPartitionRetentionSeconds ?? 3600;
        _partitionRetention = TimeSpan.FromSeconds(Math.Max(1, retentionSeconds));

        var compactEvery = options?.Value.InMemoryCompactionEveryOperations ?? 256;
        _compactEveryOperations = Math.Max(1, compactEvery);
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

        var state = _requestWindows.GetOrAdd(partitionKey, _ => new RequestWindowState(now));

        // Refill, the limit decision and the debit happen under one lock, so a rejected request
        // never consumes a token and the decision is always made against the current fill.
        var acquired = state.TryTake(now, capacity, refillPerSecond: Math.Max(1, policy.Rpm) / 60.0, out var retryAfter);

        // Compaction runs on both outcomes: a partition stuck in permanent rejection would otherwise
        // never be swept.
        CompactIfNeeded(now);

        if (acquired)
        {
            return new RateLimitAcquireResult(true);
        }

        return new RateLimitAcquireResult(
            false,
            GatewayRateLimitReason.RateLimitExceeded,
            Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
    }

    public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy)
    {
        if (policy.MaxConcurrentStreams <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        var now = DateTimeOffset.UtcNow;

        // Eviction marks a state as tombstoned and then removes it from the dictionary. In the
        // window between those two steps GetOrAdd can hand back the tombstone, whose TryAcquire
        // always fails — producing a spurious 429 for a tenant with zero active streams. Retry
        // until a live state is obtained; the tombstoned entry is on its way out.
        StreamConcurrencyState state;
        bool acquired;
        while (true)
        {
            state = _streamSlots.GetOrAdd(partitionKey, _ => new StreamConcurrencyState(now));
            if (state.TryAcquire(policy.MaxConcurrentStreams, now, out acquired))
            {
                break;
            }

            // Tombstoned: help it out of the dictionary so the next GetOrAdd creates a fresh state.
            _streamSlots.TryRemove(new KeyValuePair<string, StreamConcurrencyState>(partitionKey, state));
        }

        if (!acquired)
        {
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.ConcurrencyLimitExceeded,
                RetryAfterSeconds: 1);
        }

        CompactIfNeeded(now);
        return new RateLimitAcquireResult(true);
    }

    public void ReleaseStreamSlot(string partitionKey)
    {
        if (_streamSlots.TryGetValue(partitionKey, out var state))
        {
            state.Release(DateTimeOffset.UtcNow);
        }
    }

    private void CompactIfNeeded(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _operationCount) % _compactEveryOperations != 0)
        {
            return;
        }

        var staleThreshold = now - _partitionRetention;

        foreach (var pair in _requestWindows)
        {
            if (pair.Value.IsStale(staleThreshold))
            {
                _requestWindows.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _streamSlots)
        {
            if (pair.Value.TryMarkEvicted(staleThreshold))
            {
                _streamSlots.TryRemove(pair.Key, out _);
            }
        }
    }

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

        /// <summary>
        /// Refills the bucket for the time elapsed since the last call, then takes one token if one
        /// is available. When it is not, <paramref name="retryAfter"/> is how long until the next
        /// token arrives at the current refill rate.
        /// </summary>
        public bool TryTake(DateTimeOffset now, int capacity, double refillPerSecond, out TimeSpan retryAfter)
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

                var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    _tokens = Math.Min(capacity, _tokens + (elapsedSeconds * refillPerSecond));
                    _lastRefillUtc = now;
                }
                else if (elapsedSeconds < 0)
                {
                    // Clock went backwards (or callers supplied out-of-order timestamps); do not
                    // let a stale "last refill" grant a windfall when time catches up.
                    _lastRefillUtc = now;
                }

                // Touch on every attempt, including rejections, so an actively-rejected partition is
                // not treated as stale and evicted from under itself.
                _lastSeenUtc = now;

                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                var deficit = 1.0 - _tokens;
                retryAfter = TimeSpan.FromSeconds(deficit / refillPerSecond);
                return false;
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

        /// <summary>
        /// Attempts to take a slot. Returns false when this state has been tombstoned by compaction,
        /// which the caller must distinguish from a genuine limit rejection (reported via
        /// <paramref name="acquired"/>) — conflating the two is what produced spurious 429s.
        /// </summary>
        public bool TryAcquire(int maxConcurrent, DateTimeOffset now, out bool acquired)
        {
            lock (_sync)
            {
                if (_evicted)
                {
                    acquired = false;
                    return false;
                }

                if (_active >= maxConcurrent)
                {
                    acquired = false;
                    return true;
                }

                _active++;
                _lastSeenUtc = now;
                acquired = true;
                return true;
            }
        }

        public void Release(DateTimeOffset now)
        {
            lock (_sync)
            {
                _active = Math.Max(0, _active - 1);
                _lastSeenUtc = now;
            }
        }

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
