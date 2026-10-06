using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Pol33.Core.Observability;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Streaming responses open right now per caller, and the most each caller has ever had open at
/// once: the two numbers a per-key stream cap is set from.
/// </summary>
/// <remarks>
/// <para>A stream cap below a key's normal peak refuses traffic that is admitted today, and until
/// this existed nothing recorded that peak, so caps could only be guessed. The current count is
/// sampled at scrape time and so misses a burst shorter than the scrape interval; the peak is kept
/// here, on every stream start, and misses nothing.</para>
///
/// <para>Callers arrive already resolved by <see cref="MetricCallerBudget"/>, so the table is bounded
/// by the same ceiling as every other caller series. An entry is never removed: a caller that has
/// streamed once keeps a series reading zero, which is what lets a query tell "no streams open" from
/// "never seen".</para>
/// </remarks>
public sealed class CallerOpenStreams
{
    private readonly ConcurrentDictionary<MetricCaller, Count> _open = new();

    /// <summary>The counter for <paramref name="caller"/>. No allocation for a caller already seen.</summary>
    public Count For(MetricCaller caller) => _open.GetOrAdd(caller, static _ => new Count());

    public IEnumerable<Measurement<long>> ObserveOpen()
    {
        foreach (var (caller, count) in _open)
        {
            yield return new Measurement<long>(count.Open, Tags(caller));
        }
    }

    public IEnumerable<Measurement<long>> ObservePeak()
    {
        foreach (var (caller, count) in _open)
        {
            yield return new Measurement<long>(count.Peak, Tags(caller));
        }
    }

    private static KeyValuePair<string, object?>[] Tags(MetricCaller caller) =>
    [
        new("tenant", caller.Tenant),
        new("key", caller.Key),
    ];

    public sealed class Count
    {
        private long _open;
        private long _peak;

        public long Open => Interlocked.Read(ref _open);

        /// <summary>The most streams open at once since the process started.</summary>
        public long Peak => Interlocked.Read(ref _peak);

        public void StreamStarted()
        {
            var open = Interlocked.Increment(ref _open);
            var peak = Interlocked.Read(ref _peak);
            while (open > peak)
            {
                var seen = Interlocked.CompareExchange(ref _peak, open, peak);
                if (seen == peak)
                {
                    break;
                }

                peak = seen;
            }
        }

        public void StreamEnded() => Interlocked.Decrement(ref _open);
    }
}
