using System.Collections.Concurrent;
using Pol33.Core.Models.Overview;

namespace Pol33.Observability.Runtime;

/// <summary>
/// A bounded set of named per-minute counters over the last 24 hours: "requests by tenant",
/// "rejections by reason" and the like. Reads are O(keys × 1440) and allocation-free on the write
/// path; past <see cref="MaxKeys"/> distinct keys new ones are ignored rather than evicted, so a
/// flood of one-off keys cannot push out the ones an operator is watching.
/// </summary>
public sealed class CountDimension(int maxKeys = CountDimension.DefaultMaxKeys)
{
    public const int DefaultMaxKeys = 200;
    private const int Slots = 1440;

    private readonly ConcurrentDictionary<string, Ring> _keys = new(StringComparer.Ordinal);

    public int MaxKeys { get; } = Math.Max(1, maxKeys);

    public void Add(string key, DateTimeOffset now, long amount = 1)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!_keys.TryGetValue(key, out var ring))
        {
            if (_keys.Count >= MaxKeys)
            {
                return;
            }

            ring = _keys.GetOrAdd(key, static _ => new Ring());
        }

        ring.Add(now, amount);
    }

    public IReadOnlyList<CountRow> Top(DateTimeOffset now, int minutes, int take)
    {
        var rows = new List<CountRow>();
        foreach (var (key, ring) in _keys)
        {
            var count = ring.Sum(now, minutes);
            if (count > 0)
            {
                rows.Add(new CountRow(key, count));
            }
        }

        rows.Sort(static (a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Key, b.Key));
        if (rows.Count > take)
        {
            rows.RemoveRange(take, rows.Count - take);
        }

        return rows;
    }

    public void Clear() => _keys.Clear();

    private sealed class Ring
    {
        private readonly long[] _stamps = new long[Slots];
        private readonly long[] _counts = new long[Slots];
        private readonly object _sync = new();

        public void Add(DateTimeOffset now, long amount)
        {
            var stamp = now.ToUnixTimeSeconds() / 60;
            var slot = (int)(stamp % Slots);
            lock (_sync)
            {
                if (_stamps[slot] != stamp)
                {
                    _stamps[slot] = stamp;
                    _counts[slot] = 0;
                }

                _counts[slot] += amount;
            }
        }

        public long Sum(DateTimeOffset now, int minutes)
        {
            var newest = now.ToUnixTimeSeconds() / 60;
            var oldest = newest - Math.Clamp(minutes, 1, Slots) + 1;
            long total = 0;
            lock (_sync)
            {
                for (var i = 0; i < Slots; i++)
                {
                    if (_stamps[i] >= oldest && _stamps[i] <= newest)
                    {
                        total += _counts[i];
                    }
                }
            }

            return total;
        }
    }
}
