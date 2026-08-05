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

    public RateLimitAcquireResult TryAcquireRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now)
    {
        var limit = policy.Rpm + policy.Burst;
        if (limit <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        var windowStart = AlignToMinute(now);
        var state = _requestWindows.GetOrAdd(partitionKey, _ => new RequestWindowState(windowStart, now));

        // Window rollover, the limit decision and the increment happen under one lock. Splitting
        // them (AddOrUpdate then read Count) meant a rollover between the two could have the decision
        // judged against a different window's count, and the increment landed before the check — so a
        // client that kept hammering after a 429 pushed the counter further out of reach and could
        // not recover within the window.
        var acquired = state.TryAdvance(windowStart, now, limit);

        // Compaction runs on both outcomes: a partition stuck in permanent rejection would otherwise
        // never be swept.
        CompactIfNeeded(now);

        if (acquired)
        {
            return new RateLimitAcquireResult(true);
        }

        var retryAfter = (int)Math.Ceiling((windowStart.AddMinutes(1) - now).TotalSeconds);
        return new RateLimitAcquireResult(
            false,
            GatewayRateLimitReason.RateLimitExceeded,
            Math.Max(1, retryAfter));
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

    private static DateTimeOffset AlignToMinute(DateTimeOffset now) =>
        new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

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

    private sealed class RequestWindowState
    {
        private readonly object _sync = new();
        private DateTimeOffset _windowStart;
        private DateTimeOffset _lastSeenUtc;
        private int _count;

        public RequestWindowState(DateTimeOffset windowStart, DateTimeOffset now)
        {
            _windowStart = windowStart;
            _lastSeenUtc = now;
            _count = 0;
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// Rolls the window if needed, decides whether the request fits, and consumes a slot only if
        /// it does — all under one lock, so the decision can never be made against a different
        /// window's count and a rejected request never consumes quota.
        /// </summary>
        public bool TryAdvance(DateTimeOffset windowStart, DateTimeOffset now, int limit)
        {
            lock (_sync)
            {
                if (windowStart != _windowStart)
                {
                    _windowStart = windowStart;
                    _count = 0;
                }

                // Touch on every attempt, including rejections, so an actively-rejected partition is
                // not treated as stale and evicted from under itself.
                _lastSeenUtc = now;

                if (_count >= limit)
                {
                    return false;
                }

                _count++;
                return true;
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
