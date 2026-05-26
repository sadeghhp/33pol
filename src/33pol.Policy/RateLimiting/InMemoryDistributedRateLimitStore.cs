using System.Collections.Concurrent;
using Pol33.Core.Abstractions;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

public sealed class InMemoryDistributedRateLimitStore : IDistributedRateLimitStore
{
    private readonly ConcurrentDictionary<string, RequestWindowState> _requestWindows = new();
    private readonly ConcurrentDictionary<string, StreamConcurrencyState> _streamSlots = new();

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
            _ => new RequestWindowState(windowStart, 1),
            (_, existing) => existing.Advance(windowStart, 1));

        if (state.Count > limit)
        {
            var retryAfter = (int)Math.Ceiling((windowStart.AddMinutes(1) - now).TotalSeconds);
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.RateLimitExceeded,
                Math.Max(1, retryAfter));
        }

        return new RateLimitAcquireResult(true);
    }

    public RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy)
    {
        if (policy.MaxConcurrentStreams <= 0)
        {
            return new RateLimitAcquireResult(true);
        }

        var state = _streamSlots.GetOrAdd(partitionKey, static _ => new StreamConcurrencyState());
        var acquired = state.TryAcquire(policy.MaxConcurrentStreams);
        if (!acquired)
        {
            return new RateLimitAcquireResult(
                false,
                GatewayRateLimitReason.ConcurrencyLimitExceeded,
                RetryAfterSeconds: 1);
        }

        return new RateLimitAcquireResult(true);
    }

    public void ReleaseStreamSlot(string partitionKey)
    {
        if (_streamSlots.TryGetValue(partitionKey, out var state))
        {
            state.Release();
        }
    }

    private static DateTimeOffset AlignToMinute(DateTimeOffset now) =>
        new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

    private sealed class RequestWindowState
    {
        private readonly object _sync = new();
        private DateTimeOffset _windowStart;
        private int _count;

        public RequestWindowState(DateTimeOffset windowStart, int count)
        {
            _windowStart = windowStart;
            _count = count;
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

        public RequestWindowState Advance(DateTimeOffset windowStart, int increment)
        {
            lock (_sync)
            {
                if (windowStart != _windowStart)
                {
                    _windowStart = windowStart;
                    _count = 0;
                }

                _count += increment;
            }

            return this;
        }
    }

    private sealed class StreamConcurrencyState
    {
        private int _active;

        public bool TryAcquire(int maxConcurrent)
        {
            while (true)
            {
                var current = Volatile.Read(ref _active);
                if (current >= maxConcurrent)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void Release()
        {
            var value = Interlocked.Decrement(ref _active);
            if (value < 0)
            {
                Interlocked.Exchange(ref _active, 0);
            }
        }
    }
}
