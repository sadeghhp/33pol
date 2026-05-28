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
        var state = _requestWindows.AddOrUpdate(
            partitionKey,
            _ => RequestWindowState.Create(windowStart, now),
            (_, existing) => existing.Advance(windowStart, now, 1));

        if (state.Count > limit)
        {
            var retryAfter = (int)Math.Ceiling((windowStart.AddMinutes(1) - now).TotalSeconds);
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.RateLimitExceeded,
                Math.Max(1, retryAfter));
        }

        CompactIfNeeded(now);
        return new RateLimitAcquireResult(true);
    }

    public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy)
    {
        if (policy.MaxConcurrentStreams <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        var now = DateTimeOffset.UtcNow;
        var state = _streamSlots.GetOrAdd(partitionKey, _ => new StreamConcurrencyState(now));
        var acquired = state.TryAcquire(policy.MaxConcurrentStreams, now);
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

        private RequestWindowState(DateTimeOffset windowStart, DateTimeOffset lastSeenUtc, int count)
        {
            _windowStart = windowStart;
            _lastSeenUtc = lastSeenUtc;
            _count = count;
        }

        public static RequestWindowState Create(DateTimeOffset windowStart, DateTimeOffset now) =>
            new(windowStart, now, 1);

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

        public RequestWindowState Advance(DateTimeOffset windowStart, DateTimeOffset now, int increment)
        {
            lock (_sync)
            {
                if (windowStart != _windowStart)
                {
                    _windowStart = windowStart;
                    _count = 0;
                }

                _count += increment;
                _lastSeenUtc = now;
            }

            return this;
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

        public bool TryAcquire(int maxConcurrent, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_evicted || _active >= maxConcurrent)
                {
                    return false;
                }

                _active++;
                _lastSeenUtc = now;
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
