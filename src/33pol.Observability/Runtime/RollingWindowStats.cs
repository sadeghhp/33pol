using System.Collections.Concurrent;
using Pol33.Core.Models.Overview;
using Pol33.Core.Observability;

namespace Pol33.Observability.Runtime;

/// <summary>
/// Time-bucketed request statistics for the admin Overview: trailing windows (1m/5m/1h/24h) with
/// error rate, latency and time-to-first-token percentiles, tokens, priced cost and rejections by
/// reason — gateway-wide and per model — plus a per-minute series for the sparklines.
/// </summary>
/// <remarks>
/// <para>
/// Everything lives in fixed rings: 1440 one-minute buckets (24 h) and 60 one-second buckets (the
/// 1-minute window, so it refreshes smoothly instead of jumping at minute boundaries). Buckets are
/// reused lazily — a write into a slot whose stamp is stale clears it first — so there is no timer
/// and no allocation on the hot path. Percentiles come from fixed-boundary histograms that share
/// their bins with the OpenTelemetry exporter (<see cref="LatencyHistogramBoundaries"/>); the value
/// is interpolated inside the bin, which is an approximation and is documented as such.
/// </para>
/// <para>
/// All of it is process memory: it resets on restart, deliberately, like the in-flight gauges.
/// </para>
/// </remarks>
public sealed class RollingWindowStats
{
    public const int MinuteBuckets = 1440;
    public const int SecondBuckets = 60;
    public const int PerModelCap = 20;

    private static readonly int ReasonCount = Enum.GetValues<RejectionReason>().Length;

    private readonly TimeProvider _time;
    private readonly WindowRing _global;
    private readonly ConcurrentDictionary<string, WindowRing> _perModel = new(StringComparer.OrdinalIgnoreCase);

    public RollingWindowStats(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _global = new WindowRing();
    }

    /// <summary>Upper bound on per-model rings; models past it still count gateway-wide.</summary>
    public int MaxTrackedModels { get; set; } = 64;

    public bool Enabled { get; set; } = true;

    // ---- writes ----

    public void RecordCompletion(string modelId, double durationMs, bool success, bool isStreaming)
    {
        if (!Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var ms = Math.Max(0, durationMs);
        _global.RecordCompletion(now, ms, success, isStreaming);
        Ring(modelId)?.RecordCompletion(now, ms, success, isStreaming);
    }

    public void RecordTimeToFirstToken(string modelId, double ttftMs)
    {
        if (!Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var ms = Math.Max(0, ttftMs);
        _global.RecordTtft(now, ms);
        Ring(modelId)?.RecordTtft(now, ms);
    }

    public void RecordUsage(string? modelId, long promptTokens, long completionTokens, decimal? pricedCost)
    {
        if (!Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();
        _global.RecordUsage(now, promptTokens, completionTokens, pricedCost);
        if (modelId is not null)
        {
            Ring(modelId)?.RecordUsage(now, promptTokens, completionTokens, pricedCost);
        }
    }

    /// <param name="reason">Which control refused the request; null when it is already counted under a reason elsewhere.</param>
    /// <param name="countAsFailedRequest">
    /// True for admission rejections that the lifetime counters also count as a failed request
    /// (bulkhead, circuit, unhealthy backend, grant). False for the rate-limit and quota middleware,
    /// which refuse before the request is counted at all — matching the lifetime totals.
    /// </param>
    public void RecordRejection(string? modelId, RejectionReason? reason, bool countAsFailedRequest = true)
    {
        if (!Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();
        _global.RecordRejection(now, reason, countAsFailedRequest);
        if (!string.IsNullOrEmpty(modelId))
        {
            Ring(modelId)?.RecordRejection(now, reason, countAsFailedRequest);
        }
    }

    /// <summary>Records the current in-flight count; the series keeps the per-minute peak.</summary>
    public void SampleInFlight(int inFlight)
    {
        if (!Enabled)
        {
            return;
        }

        _global.SampleInFlight(_time.GetUtcNow(), Math.Max(0, inFlight));
    }

    public void Reset()
    {
        _global.Clear();
        _perModel.Clear();
    }

    /// <summary>
    /// Zeroes error and rejection counts in every bucket while leaving requests, latency and tokens
    /// in place — the windowed counterpart of clearing the lifetime error counter, so the error-rate
    /// vital and the error-rate attention rule agree with a freshly cleared Errors tab.
    /// </summary>
    public void ResetErrors()
    {
        _global.ClearErrors();
        foreach (var ring in _perModel.Values)
        {
            ring.ClearErrors();
        }
    }

    /// <summary>Drops rings for models no longer in the registry.</summary>
    public void RetainOnly(IReadOnlySet<string> knownModelIds)
    {
        foreach (var key in _perModel.Keys)
        {
            if (!knownModelIds.Contains(key))
            {
                _perModel.TryRemove(key, out _);
            }
        }
    }

    private WindowRing? Ring(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        if (_perModel.TryGetValue(modelId, out var ring))
        {
            return ring;
        }

        if (_perModel.Count >= MaxTrackedModels)
        {
            return null;
        }

        return _perModel.GetOrAdd(modelId, static _ => new WindowRing());
    }

    // ---- reads ----

    public static readonly IReadOnlyList<(string Label, TimeSpan Span)> StandardWindows =
    [
        ("1m", TimeSpan.FromMinutes(1)),
        ("5m", TimeSpan.FromMinutes(5)),
        ("1h", TimeSpan.FromHours(1)),
        ("24h", TimeSpan.FromHours(24)),
    ];

    public IReadOnlyList<WindowStats> GetStandardWindows()
    {
        var result = new List<WindowStats>(StandardWindows.Count);
        foreach (var (label, span) in StandardWindows)
        {
            result.Add(GetWindow(span, label));
        }

        return result;
    }

    public WindowStats GetWindow(TimeSpan span, string? label = null)
    {
        var now = _time.GetUtcNow();
        var seconds = (int)Math.Clamp(Math.Round(span.TotalSeconds), 1, MinuteBuckets * 60);
        var agg = _global.Aggregate(now, seconds);

        var perModel = new List<WindowModelStats>();
        foreach (var (modelId, ring) in _perModel)
        {
            var m = ring.Aggregate(now, seconds);
            if (m.Requests == 0 && m.PromptTokens == 0 && m.CompletionTokens == 0)
            {
                continue;
            }

            perModel.Add(new WindowModelStats
            {
                ModelId = modelId,
                Requests = m.Requests,
                Errors = m.Errors,
                ErrorRate = m.Requests == 0 ? 0 : (double)m.Errors / m.Requests,
                LatencyP95Ms = Percentile(m.DurationBins, LatencyHistogramBoundaries.DurationMs, 0.95),
                TtftP95Ms = m.TtftCount == 0 ? null : Percentile(m.TtftBins, LatencyHistogramBoundaries.TimeToFirstTokenMs, 0.95),
                PromptTokens = m.PromptTokens,
                CompletionTokens = m.CompletionTokens,
                PricedCost = m.PricedCost,
            });
        }

        perModel.Sort(static (a, b) => b.Requests.CompareTo(a.Requests));
        if (perModel.Count > PerModelCap)
        {
            perModel.RemoveRange(PerModelCap, perModel.Count - PerModelCap);
        }

        var rejections = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < ReasonCount; i++)
        {
            if (agg.Rejections[i] > 0)
            {
                rejections[((RejectionReason)i).ToLabel()] = agg.Rejections[i];
            }
        }

        return new WindowStats
        {
            Window = label ?? FormatLabel(span),
            WindowSeconds = seconds,
            Requests = agg.Requests,
            Errors = agg.Errors,
            ErrorRate = agg.Requests == 0 ? 0 : (double)agg.Errors / agg.Requests,
            RequestsPerSecond = (double)agg.Requests / seconds,
            LatencyAvgMs = agg.DurationCount == 0 ? 0 : agg.DurationSumMs / agg.DurationCount,
            LatencyP50Ms = Percentile(agg.DurationBins, LatencyHistogramBoundaries.DurationMs, 0.50),
            LatencyP95Ms = Percentile(agg.DurationBins, LatencyHistogramBoundaries.DurationMs, 0.95),
            LatencyP99Ms = Percentile(agg.DurationBins, LatencyHistogramBoundaries.DurationMs, 0.99),
            TtftP50Ms = agg.TtftCount == 0 ? null : Percentile(agg.TtftBins, LatencyHistogramBoundaries.TimeToFirstTokenMs, 0.50),
            TtftP95Ms = agg.TtftCount == 0 ? null : Percentile(agg.TtftBins, LatencyHistogramBoundaries.TimeToFirstTokenMs, 0.95),
            TtftSamples = agg.TtftCount,
            PromptTokens = agg.PromptTokens,
            CompletionTokens = agg.CompletionTokens,
            PricedCost = agg.PricedCost,
            PricedRequests = agg.PricedRequests,
            RejectionsByReason = rejections,
            PerModel = perModel,
        };
    }

    /// <summary>The last <paramref name="minutes"/> one-minute buckets, oldest first, ending with the current minute.</summary>
    public OverviewSeries GetSeries(int minutes = 60)
    {
        var now = _time.GetUtcNow();
        var count = Math.Clamp(minutes, 1, MinuteBuckets);
        var points = _global.Series(now, count);
        return new OverviewSeries
        {
            StartUtc = points.Count == 0 ? now : points[0].T,
            StepSeconds = 60,
            Points = points,
        };
    }

    private static string FormatLabel(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalMinutes}m";

    /// <summary>
    /// Percentile from a fixed-boundary histogram: walks the cumulative count to the target rank and
    /// interpolates linearly inside that bin. The open-ended last bin is treated as 1.5× its lower
    /// bound. Returns 0 when the histogram is empty.
    /// </summary>
    public static double Percentile(long[] bins, double[] boundaries, double quantile)
    {
        long total = 0;
        foreach (var b in bins)
        {
            total += b;
        }

        if (total == 0)
        {
            return 0;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(total * quantile));
        long cumulative = 0;
        for (var i = 0; i < bins.Length; i++)
        {
            if (bins[i] == 0)
            {
                continue;
            }

            if (cumulative + bins[i] >= rank)
            {
                var lower = i == 0 ? 0 : boundaries[i - 1];
                var upper = i == boundaries.Length ? boundaries[^1] * 1.5 : boundaries[i];
                var fraction = (rank - cumulative - 0.5) / bins[i];
                return lower + (upper - lower) * Math.Clamp(fraction, 0, 1);
            }

            cumulative += bins[i];
        }

        return boundaries[^1];
    }

    public static int BinIndex(double[] boundaries, double value)
    {
        var index = Array.BinarySearch(boundaries, value);
        if (index >= 0)
        {
            return index; // exactly on a boundary belongs to that bin (upper-inclusive)
        }

        return ~index; // first boundary greater than value; boundaries.Length for the overflow bin
    }

    // ---- storage ----

    /// <summary>Mutable accumulator; also used as the read-side aggregate.</summary>
    internal sealed class Bucket
    {
        public long Stamp = -1;
        public long Requests;
        public long Errors;
        public long Streaming;
        public long DurationCount;
        public double DurationSumMs;
        public readonly long[] DurationBins = new long[LatencyHistogramBoundaries.DurationMs.Length + 1];
        public readonly long[] TtftBins = new long[LatencyHistogramBoundaries.TimeToFirstTokenMs.Length + 1];
        public long TtftCount;
        public long PromptTokens;
        public long CompletionTokens;
        public decimal PricedCost;
        public long PricedRequests;
        public readonly long[] Rejections = new long[ReasonCount];
        public int InFlightMax;

        public void Clear(long stamp)
        {
            Stamp = stamp;
            Requests = 0;
            Errors = 0;
            Streaming = 0;
            DurationCount = 0;
            DurationSumMs = 0;
            Array.Clear(DurationBins);
            Array.Clear(TtftBins);
            TtftCount = 0;
            PromptTokens = 0;
            CompletionTokens = 0;
            PricedCost = 0;
            PricedRequests = 0;
            Array.Clear(Rejections);
            InFlightMax = 0;
        }

        public void AddFrom(Bucket other)
        {
            Requests += other.Requests;
            Errors += other.Errors;
            Streaming += other.Streaming;
            DurationCount += other.DurationCount;
            DurationSumMs += other.DurationSumMs;
            for (var i = 0; i < DurationBins.Length; i++)
            {
                DurationBins[i] += other.DurationBins[i];
            }

            for (var i = 0; i < TtftBins.Length; i++)
            {
                TtftBins[i] += other.TtftBins[i];
            }

            TtftCount += other.TtftCount;
            PromptTokens += other.PromptTokens;
            CompletionTokens += other.CompletionTokens;
            PricedCost += other.PricedCost;
            PricedRequests += other.PricedRequests;
            for (var i = 0; i < Rejections.Length; i++)
            {
                Rejections[i] += other.Rejections[i];
            }

            InFlightMax = Math.Max(InFlightMax, other.InFlightMax);
        }
    }

    internal sealed class WindowRing
    {
        private readonly object _sync = new();
        private readonly Bucket[] _minutes = new Bucket[MinuteBuckets];
        private readonly Bucket[] _seconds = new Bucket[SecondBuckets];

        public WindowRing()
        {
            for (var i = 0; i < _minutes.Length; i++)
            {
                _minutes[i] = new Bucket();
            }

            for (var i = 0; i < _seconds.Length; i++)
            {
                _seconds[i] = new Bucket();
            }
        }

        public void ClearErrors()
        {
            lock (_sync)
            {
                foreach (var bucket in _minutes)
                {
                    bucket.Errors = 0;
                    Array.Clear(bucket.Rejections);
                }

                foreach (var bucket in _seconds)
                {
                    bucket.Errors = 0;
                    Array.Clear(bucket.Rejections);
                }
            }
        }

        private static long MinuteStamp(DateTimeOffset now) => now.ToUnixTimeSeconds() / 60;

        private static long SecondStamp(DateTimeOffset now) => now.ToUnixTimeSeconds();

        private Bucket Minute(DateTimeOffset now)
        {
            var stamp = MinuteStamp(now);
            var bucket = _minutes[(int)(stamp % MinuteBuckets)];
            if (bucket.Stamp != stamp)
            {
                bucket.Clear(stamp);
            }

            return bucket;
        }

        private Bucket Second(DateTimeOffset now)
        {
            var stamp = SecondStamp(now);
            var bucket = _seconds[(int)(stamp % SecondBuckets)];
            if (bucket.Stamp != stamp)
            {
                bucket.Clear(stamp);
            }

            return bucket;
        }

        public void RecordCompletion(DateTimeOffset now, double durationMs, bool success, bool isStreaming)
        {
            var bin = BinIndex(LatencyHistogramBoundaries.DurationMs, durationMs);
            lock (_sync)
            {
                Apply(Minute(now));
                Apply(Second(now));
            }

            void Apply(Bucket b)
            {
                b.Requests++;
                if (!success)
                {
                    b.Errors++;
                }

                if (isStreaming)
                {
                    b.Streaming++;
                }

                b.DurationCount++;
                b.DurationSumMs += durationMs;
                b.DurationBins[bin]++;
            }
        }

        public void RecordTtft(DateTimeOffset now, double ttftMs)
        {
            var bin = BinIndex(LatencyHistogramBoundaries.TimeToFirstTokenMs, ttftMs);
            lock (_sync)
            {
                var m = Minute(now);
                m.TtftBins[bin]++;
                m.TtftCount++;
                var s = Second(now);
                s.TtftBins[bin]++;
                s.TtftCount++;
            }
        }

        public void RecordUsage(DateTimeOffset now, long promptTokens, long completionTokens, decimal? pricedCost)
        {
            lock (_sync)
            {
                Apply(Minute(now));
                Apply(Second(now));
            }

            void Apply(Bucket b)
            {
                b.PromptTokens += Math.Max(0, promptTokens);
                b.CompletionTokens += Math.Max(0, completionTokens);
                if (pricedCost is { } cost)
                {
                    b.PricedCost += cost;
                    b.PricedRequests++;
                }
            }
        }

        public void RecordRejection(DateTimeOffset now, RejectionReason? reason, bool countAsFailedRequest)
        {
            var index = reason is { } r ? (int)r : -1;
            if (index >= ReasonCount)
            {
                return;
            }

            lock (_sync)
            {
                Apply(Minute(now));
                Apply(Second(now));
            }

            void Apply(Bucket b)
            {
                if (countAsFailedRequest)
                {
                    // Counts toward requests and errors (matching the lifetime counters) but not latency.
                    b.Requests++;
                    b.Errors++;
                }

                if (index >= 0)
                {
                    b.Rejections[index]++;
                }
            }
        }

        public void SampleInFlight(DateTimeOffset now, int inFlight)
        {
            lock (_sync)
            {
                var m = Minute(now);
                m.InFlightMax = Math.Max(m.InFlightMax, inFlight);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                foreach (var b in _minutes)
                {
                    b.Clear(-1);
                }

                foreach (var b in _seconds)
                {
                    b.Clear(-1);
                }
            }
        }

        /// <summary>Sums the buckets covering the trailing <paramref name="seconds"/>, ending now.</summary>
        public Bucket Aggregate(DateTimeOffset now, int seconds)
        {
            var result = new Bucket();
            lock (_sync)
            {
                if (seconds <= SecondBuckets)
                {
                    var newest = SecondStamp(now);
                    var oldest = newest - seconds + 1;
                    foreach (var b in _seconds)
                    {
                        if (b.Stamp >= oldest && b.Stamp <= newest)
                        {
                            result.AddFrom(b);
                        }
                    }
                }
                else
                {
                    var newest = MinuteStamp(now);
                    var minutes = (seconds + 59) / 60;
                    var oldest = newest - minutes + 1;
                    foreach (var b in _minutes)
                    {
                        if (b.Stamp >= oldest && b.Stamp <= newest)
                        {
                            result.AddFrom(b);
                        }
                    }
                }
            }

            return result;
        }

        public List<OverviewSeriesPoint> Series(DateTimeOffset now, int minutes)
        {
            var newest = MinuteStamp(now);
            var oldest = newest - minutes + 1;
            var points = new List<OverviewSeriesPoint>(minutes);
            lock (_sync)
            {
                for (var stamp = oldest; stamp <= newest; stamp++)
                {
                    var b = _minutes[(int)(stamp % MinuteBuckets)];
                    var t = DateTimeOffset.FromUnixTimeSeconds(stamp * 60);
                    if (b.Stamp != stamp)
                    {
                        points.Add(new OverviewSeriesPoint { T = t });
                        continue;
                    }

                    points.Add(new OverviewSeriesPoint
                    {
                        T = t,
                        Requests = b.Requests,
                        Errors = b.Errors,
                        LatencyP95Ms = Percentile(b.DurationBins, LatencyHistogramBoundaries.DurationMs, 0.95),
                        TtftP95Ms = b.TtftCount == 0 ? null : Percentile(b.TtftBins, LatencyHistogramBoundaries.TimeToFirstTokenMs, 0.95),
                        InFlight = b.InFlightMax,
                        Tokens = b.PromptTokens + b.CompletionTokens,
                        Cost = b.PricedCost,
                    });
                }
            }

            return points;
        }
    }
}
