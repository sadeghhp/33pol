using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Observability.RateLimiting;

/// <summary>
/// Per-minute admission counters for every user, model and user×model pair, plus where limits are
/// actually being hit — the data behind <c>GET /admin/api/rate-limits/usage</c>.
/// </summary>
/// <remarks>
/// <para>Each tracked key holds a fixed ring of <see cref="WindowMinutes"/> per-minute slots. A write
/// is one hash lookup and three interlocked adds into a slot the key already owns: no allocation, no
/// lock held across the request path, and the same memory whether the gateway is idle or saturated.
/// A read walks the ring, so building a report is O(keys) and never touches the request path at
/// all.</para>
///
/// <para>Keys are bounded per section. Past the ceiling a new key is <em>ignored</em> rather than
/// evicting an existing one, which is the deliberate choice: eviction would let a flood of one-off
/// callers push out the tenants an operator is watching, turning the report blank exactly when it
/// matters. The cost is that a brand-new tenant may not appear until the table has room, which the
/// report states rather than hides.</para>
///
/// <para>Nothing here is durable. Long-horizon usage — tokens, cost, per tenant and model, over
/// months — already lives in the billing rollups. This answers the question those cannot: how each
/// caller sits against its <em>limits</em> right now.</para>
/// </remarks>
public sealed class RateLimitUsageTracker : IRateLimitUsageTracker
{
    /// <summary>
    /// How far back the counters reach. Three hours covers the windows an operator asks for while
    /// keeping a tracked key to a few kilobytes.
    /// </summary>
    public const int WindowMinutes = 180;

    private readonly TimeProvider _time;
    private readonly int _maxKeys;

    private readonly UsageDimension _byTenantModel;
    private readonly UsageDimension _byTenant;
    private readonly UsageDimension _byModel;
    private readonly UsageDimension _byApiKey;
    private readonly ConcurrentDictionary<ViolationKey, Counter> _violations = new();
    private readonly DropCounter _violationDrops = new();

    /// <summary>
    /// Every decision, in a ring no key ceiling applies to. The totals and the gateway-wide series
    /// are read from here, so they stay exact while a per-subject section is full.
    /// </summary>
    private readonly LimitRing _totals = new();

    /// <summary>Per configured limit, keyed by control id rather than by bucket.</summary>
    private readonly ConcurrentDictionary<LimitKey, LimitRing> _limits = new();
    private readonly DropCounter _limitDrops = new();

    // Reserved, outside the key ceiling: two rows that must never read as a false zero.
    private readonly LimitRing _authFailure = new();
    private readonly LimitRing _anonymous = new();

    private long _trackingSinceTicks;

    private readonly IAdaptiveRateLimitGovernor? _governor;
    private readonly IDistributedRateLimitStore? _store;

    public RateLimitUsageTracker(
        IOptions<RateLimitingOptions>? options = null,
        IAdaptiveRateLimitGovernor? governor = null,
        IDistributedRateLimitStore? store = null,
        TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _governor = governor;
        _store = store;
        _maxKeys = Math.Clamp(options?.Value.UsageReportMaxKeys ?? 500, 10, 20_000);

        _byTenantModel = new UsageDimension(_maxKeys);
        _byTenant = new UsageDimension(_maxKeys);
        _byModel = new UsageDimension(_maxKeys);
        _byApiKey = new UsageDimension(_maxKeys);
        _trackingSinceTicks = _time.GetUtcNow().UtcTicks;
    }

    public void Record(in RateLimitUsageEvent usageEvent)
    {
        var now = _time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60;
        var admitted = usageEvent.Admitted;

        // Only meaningful on a refusal: an admitted decision carries the rate control by default
        // whether or not a concurrency cap was involved.
        var concurrencyRejection = !admitted && usageEvent.Control == RateLimitControl.Concurrency;

        var tenant = usageEvent.TenantId;
        var model = usageEvent.ModelId;
        var apiKey = usageEvent.ApiKeyId;

        _totals.AddDecision(minute, admitted, concurrencyRejection);

        if (!string.IsNullOrEmpty(tenant))
        {
            _byTenant.Add(tenant, now, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);

            if (!string.IsNullOrEmpty(model))
            {
                _byTenantModel.Add(
                    RateLimitKeys.Pair(tenant, model),
                    now,
                    minute,
                    admitted,
                    concurrencyRejection,
                    usageEvent.ConfiguredRpm,
                    usageEvent.EffectiveRpm);
            }
        }

        if (!string.IsNullOrEmpty(model))
        {
            _byModel.Add(model, now, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            _byApiKey.Add(apiKey, now, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);
        }

        if (!admitted && usageEvent.Scope is { } scope)
        {
            RecordViolation(scope, usageEvent.Control, tenant, apiKey, model, now);
        }
    }

    /// <summary>
    /// Counts one limit hit against the scope that refused, keyed by the identity that scope
    /// actually counts — a tenant-scope hit is attributed to the tenant, a model-scope hit to the
    /// model, and so on. Attributing every hit to the caller would make a saturated model look like
    /// a hundred misbehaving tenants.
    /// </summary>
    private void RecordViolation(
        RateLimitScope scope,
        RateLimitControl control,
        string? tenant,
        string? apiKey,
        string? model,
        DateTimeOffset now)
    {
        var subject = scope switch
        {
            RateLimitScope.Global => "*",
            RateLimitScope.Tenant => tenant,
            RateLimitScope.ApiKey => apiKey,
            RateLimitScope.Model => model,
            RateLimitScope.TenantModel => tenant is null || model is null ? null : RateLimitKeys.Pair(tenant, model),
            RateLimitScope.ApiKeyModel => apiKey is null || model is null ? null : RateLimitKeys.Pair(apiKey, model),
            _ => null,
        };

        if (string.IsNullOrEmpty(subject))
        {
            return;
        }

        var key = new ViolationKey(scope, subject, control);
        if (!_violations.TryGetValue(key, out var counter))
        {
            if (_violations.Count >= _maxKeys)
            {
                _violationDrops.Record(now);
                return;
            }

            counter = _violations.GetOrAdd(key, static _ => new Counter());
        }

        counter.Increment();
    }

    public RateLimitUsageReport BuildReport(int minutes, int take, DateTimeOffset now)
    {
        var window = Math.Clamp(minutes, 1, WindowMinutes);
        var rows = Math.Clamp(take, 1, 1000);
        var newest = now.ToUnixTimeSeconds() / 60;
        var oldest = newest - window + 1;

        var byTenantModel = _byTenantModel.Top(oldest, newest, rows, window, static key =>
        {
            RateLimitKeys.TrySplitPair(key, out var tenant, out var model);
            return (tenant.Length == 0 ? null : tenant, null, model.Length == 0 ? null : model);
        });

        var byTenant = _byTenant.Top(oldest, newest, rows, window, static key => (key, null, null));
        var byModel = _byModel.Top(oldest, newest, rows, window, static key => (null, null, key));
        var byApiKey = _byApiKey.Top(oldest, newest, rows, window, static key => (null, key, null));

        var all = _totals.Sum(oldest, newest);
        var totals = UsageDimension.ToTotals((all.Evaluations, all.Charged, all.RefusedByStreams));

        var violations = _violations
            .Select(pair => new RateLimitViolationRow(
                pair.Key.Scope.ToLabel(),
                pair.Key.Subject,
                pair.Key.Control == RateLimitControl.Concurrency ? "concurrency" : "rate",
                pair.Value.Value))
            .Where(static row => row.Hits > 0)
            .OrderByDescending(static row => row.Hits)
            .ThenBy(static row => row.Key, StringComparer.Ordinal)
            .Take(rows)
            .ToArray();

        return new RateLimitUsageReport(
            window,
            now,
            totals,
            byTenantModel,
            byTenant,
            byModel,
            byApiKey,
            violations,
            BuildAdaptiveReport(),
            BuildStoreReport())
        {
            Limits = BuildLimitRows(oldest, newest, window),
            Protective =
            [
                ToProtectiveRow(RateLimitScopeNames.AuthFailure, RateLimitLimitIds.AuthFailure, _authFailure, oldest, newest),
                ToProtectiveRow(RateLimitScopeNames.Anonymous, RateLimitLimitIds.Anonymous, _anonymous, oldest, newest),
            ],
            Tracker = BuildTrackerReport(),
        };
    }

    public void RecordRateStage(
        ReadOnlySpan<RateLimitRule> rules,
        RateLimitStageOutcome outcome,
        string? refusedPartitionKey = null)
    {
        var now = _time.GetUtcNow();
        var second = now.ToUnixTimeSeconds();
        var minute = second / 60;

        foreach (ref readonly var rule in rules)
        {
            // The store skips a rule with no capacity, so it was never evaluated.
            if (rule.LimitId is null || rule.Policy.Capacity <= 0)
            {
                continue;
            }

            var refusedHere = outcome == RateLimitStageOutcome.Refused &&
                              string.Equals(rule.PartitionKey, refusedPartitionKey, StringComparison.Ordinal);

            var ring = RingFor(rule.LimitId, rule.AnonymousBucket, now);
            ring?.AddRate(
                minute,
                second,
                charged: outcome == RateLimitStageOutcome.Charged,
                refused: refusedHere,
                rule.ConfiguredRpm,
                rule.Policy.Rpm);

            if (refusedHere)
            {
                // Rules after the one that refused were never asked.
                break;
            }
        }
    }

    public void RecordStreamStage(ReadOnlySpan<RateLimitRule> rules, string? refusedPartitionKey = null)
    {
        var now = _time.GetUtcNow();
        var second = now.ToUnixTimeSeconds();
        var minute = second / 60;

        foreach (ref readonly var rule in rules)
        {
            var limitId = rule.StreamLimitId ?? rule.LimitId;
            if (limitId is null || !rule.Policy.EnforcesConcurrency)
            {
                continue;
            }

            var refusedHere = refusedPartitionKey is not null &&
                              string.Equals(rule.PartitionKey, refusedPartitionKey, StringComparison.Ordinal);

            // A slot taken and handed straight back because a later cap was full is not a stream
            // that started, so on a refusal only the cap that refused is written to.
            if (refusedPartitionKey is null || refusedHere)
            {
                RingFor(limitId, rule.AnonymousBucket, now)?.AddStream(minute, second, refusedHere);
            }

            if (refusedHere)
            {
                break;
            }
        }
    }

    public void RecordAuthFailure(RateLimitAuthFailureStep step, int enforcedRpm)
    {
        var second = _time.GetUtcNow().ToUnixTimeSeconds();
        _authFailure.AddAuthFailure(second / 60, second, step, enforcedRpm);
    }

    public RateLimitUsageSeries? BuildSeries(
        int minutes,
        int bucketMinutes,
        string? limitId,
        bool anonymousBucket,
        DateTimeOffset now)
    {
        var window = Math.Clamp(minutes, 1, WindowMinutes);
        var width = Math.Clamp(bucketMinutes, 1, 60);
        var newest = now.ToUnixTimeSeconds() / 60;

        // Whole buckets, aligned to the epoch, so two polls a few seconds apart agree on every
        // boundary and a chart does not shimmer. The window is rounded up to cover what was asked.
        var lastBucket = newest / width;
        var firstBucket = (newest - window + 1) / width;
        var oldest = Math.Max(firstBucket * width, newest - WindowMinutes + 1);
        firstBucket = (oldest + width - 1) / width;
        oldest = firstBucket * width;

        var count = (int)(newest - oldest + 1);
        if (count <= 0)
        {
            return null;
        }

        var perMinute = new MinuteSample[count];
        if (string.IsNullOrEmpty(limitId))
        {
            _totals.Fill(oldest, perMinute);
        }
        else
        {
            var id = limitId.ToLowerInvariant();
            var ring = id == RateLimitLimitIds.AuthFailure ? _authFailure
                : id == RateLimitLimitIds.Anonymous ? _anonymous
                : _limits.GetValueOrDefault(new LimitKey(id, anonymousBucket));
            if (ring is null)
            {
                return null;
            }

            ring.Fill(oldest, perMinute);
        }

        var since = new DateTimeOffset(Volatile.Read(ref _trackingSinceTicks), TimeSpan.Zero);
        var points = new List<RateLimitUsagePoint>((int)(lastBucket - firstBucket + 1));
        for (var bucket = firstBucket; bucket <= lastBucket; bucket++)
        {
            long decisions = 0, admitted = 0, rate = 0, streams = 0;
            for (var m = bucket * width; m < (bucket + 1) * width && m <= newest; m++)
            {
                var sample = perMinute[m - oldest];
                decisions += sample.Decisions;
                admitted += sample.Admitted;
                rate += sample.RefusedByRate;
                streams += sample.RefusedByStreams;
            }

            var start = DateTimeOffset.FromUnixTimeSeconds(bucket * width * 60);
            points.Add(new RateLimitUsagePoint(
                start,
                Covered: start.AddMinutes(width) > since,
                decisions,
                admitted,
                rate,
                streams));
        }

        return new RateLimitUsageSeries(
            string.IsNullOrEmpty(limitId) ? "gateway" : "limit",
            string.IsNullOrEmpty(limitId) ? null : limitId.ToLowerInvariant(),
            !string.IsNullOrEmpty(limitId) && anonymousBucket,
            width,
            DateTimeOffset.FromUnixTimeSeconds(firstBucket * width * 60),
            DateTimeOffset.FromUnixTimeSeconds((lastBucket + 1) * width * 60),
            since,
            points);
    }

    private LimitRing? RingFor(string limitId, bool anonymousBucket, DateTimeOffset now)
    {
        if (ReferenceEquals(limitId, RateLimitLimitIds.Anonymous) || limitId == RateLimitLimitIds.Anonymous)
        {
            return _anonymous;
        }

        var key = new LimitKey(limitId, anonymousBucket);
        if (_limits.TryGetValue(key, out var ring))
        {
            return ring;
        }

        if (_limits.Count >= _maxKeys)
        {
            _limitDrops.Record(now);
            return null;
        }

        return _limits.GetOrAdd(key, static _ => new LimitRing());
    }

    private IReadOnlyList<RateLimitLimitUsageRow> BuildLimitRows(long oldest, long newest, int window)
    {
        var rows = new List<RateLimitLimitUsageRow>(_limits.Count);
        foreach (var (key, ring) in _limits)
        {
            var sum = ring.Sum(oldest, newest);
            if (sum.Evaluations == 0 && sum.StreamsStarted == 0 && sum.RefusedByStreams == 0)
            {
                continue;
            }

            var (scope, target) = RateLimitLimitIds.Split(key.LimitId);
            var single = RateLimitLimitIds.HasSingleBucket(scope);
            var effective = ring.EffectiveRpm;

            rows.Add(new RateLimitLimitUsageRow(
                key.LimitId,
                scope,
                target,
                key.AnonymousBucket,
                single,
                sum.Evaluations,
                sum.Charged,
                sum.RefusedByRate,
                sum.Evaluations - sum.Charged - sum.RefusedByRate,
                sum.StreamsStarted,
                sum.RefusedByStreams,
                (double)sum.Charged / window,
                sum.PeakCharged,
                sum.PeakCharged > 0 ? DateTimeOffset.FromUnixTimeSeconds(sum.PeakMinute * 60) : null,
                ring.ConfiguredRpm,
                effective,
                single && effective > 0 ? (double)sum.PeakCharged / effective : null,
                ring.LastDecisionUtc));
        }

        rows.Sort(static (a, b) =>
            b.Evaluations != a.Evaluations
                ? b.Evaluations.CompareTo(a.Evaluations)
                : string.CompareOrdinal(a.LimitId, b.LimitId));
        return rows;
    }

    private static RateLimitProtectiveUsageRow ToProtectiveRow(
        string scope,
        string limitId,
        LimitRing ring,
        long oldest,
        long newest)
    {
        var sum = ring.Sum(oldest, newest);
        return new RateLimitProtectiveUsageRow(
            scope,
            limitId,
            sum.Evaluations,
            sum.Charged,
            sum.RefusedByRate,
            ring.EffectiveRpm,
            sum.RefusedByStreams,
            ring.LastDecisionUtc);
    }

    private RateLimitTrackerReport BuildTrackerReport()
    {
        RateLimitTrackerDimension[] dimensions =
        [
            _byTenant.Describe("tenants"),
            _byModel.Describe("models"),
            _byApiKey.Describe("apiKeys"),
            _byTenantModel.Describe("tenantModels"),
            _violationDrops.Describe("violations", _violations.Count, _maxKeys),
            _limitDrops.Describe("limits", _limits.Count, _maxKeys),
        ];

        return new RateLimitTrackerReport(
            new DateTimeOffset(Volatile.Read(ref _trackingSinceTicks), TimeSpan.Zero),
            _maxKeys,
            dimensions.Any(static d => d.DroppedDecisions > 0),
            dimensions);
    }

    public void Reset()
    {
        _byTenantModel.Clear();
        _byTenant.Clear();
        _byModel.Clear();
        _byApiKey.Clear();
        _violations.Clear();
        _violationDrops.Clear();
        _limits.Clear();
        _limitDrops.Clear();
        _totals.Clear();
        _authFailure.Clear();
        _anonymous.Clear();
        Volatile.Write(ref _trackingSinceTicks, _time.GetUtcNow().UtcTicks);
    }

    private AdaptiveRateLimitReport BuildAdaptiveReport()
    {
        var snapshot = _governor?.Snapshot() ?? AdaptiveRateLimitSnapshot.Disabled;
        return new AdaptiveRateLimitReport(
            snapshot.Enabled,
            snapshot.LastEvaluatedUtc,
            snapshot.BackedOffPartitions,
            [.. snapshot.Models.Select(static m =>
                new AdaptiveModelRow(m.ModelId, m.Factor, m.Saturation, m.Reason, m.UpdatedUtc))]);
    }

    private RateLimitStoreReport BuildStoreReport()
    {
        var stats = _store?.GetStats() ?? default;
        return new RateLimitStoreReport(stats.RequestPartitions, stats.StreamPartitions, stats.MaxPartitions);
    }

    private readonly record struct ViolationKey(RateLimitScope Scope, string Subject, RateLimitControl Control);

    private readonly record struct LimitKey(string LimitId, bool AnonymousBucket);

    private readonly record struct MinuteSample(long Decisions, long Admitted, long RefusedByRate, long RefusedByStreams);

    /// <summary>What a full dimension turned away. Counted, because it is all that can be known about it.</summary>
    private sealed class DropCounter
    {
        private long _dropped;
        private long _firstTicks;

        public void Record(DateTimeOffset now)
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.CompareExchange(ref _firstTicks, now.UtcTicks, 0);
        }

        public void Clear()
        {
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _firstTicks, 0);
        }

        public RateLimitTrackerDimension Describe(string name, int tracked, int maxKeys)
        {
            var first = Interlocked.Read(ref _firstTicks);
            return new RateLimitTrackerDimension(
                name,
                tracked,
                maxKeys,
                tracked >= maxKeys,
                Interlocked.Read(ref _dropped),
                first == 0 ? null : new DateTimeOffset(first, TimeSpan.Zero));
        }
    }

    /// <summary>Per-minute counters for one configured limit. Ints: a minute cannot hold 2^31 decisions.</summary>
    /// <remarks>
    /// Writes are interlocked increments, not a lock. A limit's ring is shared by every request that
    /// limit applies to — the global rule's by all of them — so a lock here would be the one place
    /// every request in the process queues. The lock is taken only to roll a slot over to a new
    /// minute, once per minute per ring: counters are zeroed first and the minute published last, so
    /// a writer on the fast path never increments a slot that is about to be cleared. A reader may
    /// see the minute in progress mid-update; it never sees a count from another minute.
    /// </remarks>
    private sealed class LimitRing
    {
        private readonly long[] _minutes = new long[WindowMinutes];
        private readonly int[] _evaluations = new int[WindowMinutes];
        private readonly int[] _charged = new int[WindowMinutes];
        private readonly int[] _refusedByRate = new int[WindowMinutes];
        private readonly int[] _streamsStarted = new int[WindowMinutes];
        private readonly int[] _refusedByStreams = new int[WindowMinutes];
        private readonly object _sync = new();

        private int _configuredRpm;
        private int _effectiveRpm;
        private long _lastSecond;

        public int ConfiguredRpm => Volatile.Read(ref _configuredRpm);

        public int EffectiveRpm => Volatile.Read(ref _effectiveRpm);

        public DateTimeOffset? LastDecisionUtc
        {
            get
            {
                var second = Volatile.Read(ref _lastSecond);
                return second == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(second);
            }
        }

        public void AddRate(long minute, long second, bool charged, bool refused, int configuredRpm, int effectiveRpm)
        {
            var slot = Claim(minute);
            Interlocked.Increment(ref _evaluations[slot]);
            if (charged)
            {
                Interlocked.Increment(ref _charged[slot]);
            }
            else if (refused)
            {
                Interlocked.Increment(ref _refusedByRate[slot]);
            }

            // Exact for this limit, unlike the per-subject rows: every bucket a control owns is held
            // to the same tier, so whichever request wrote last wrote the same numbers.
            Volatile.Write(ref _configuredRpm, configuredRpm);
            Volatile.Write(ref _effectiveRpm, effectiveRpm);
            Volatile.Write(ref _lastSecond, second);
        }

        /// <summary>One gateway-wide admission decision, for the ring the totals are read from.</summary>
        public void AddDecision(long minute, bool admitted, bool concurrencyRejection)
        {
            var slot = Claim(minute);
            Interlocked.Increment(ref _evaluations[slot]);
            if (admitted)
            {
                Interlocked.Increment(ref _charged[slot]);
            }
            else if (concurrencyRejection)
            {
                Interlocked.Increment(ref _refusedByStreams[slot]);
            }
            else
            {
                Interlocked.Increment(ref _refusedByRate[slot]);
            }
        }

        public void AddStream(long minute, long second, bool refused)
        {
            var slot = Claim(minute);
            if (refused)
            {
                Interlocked.Increment(ref _refusedByStreams[slot]);
            }
            else
            {
                Interlocked.Increment(ref _streamsStarted[slot]);
            }

            Volatile.Write(ref _lastSecond, second);
        }

        public void AddAuthFailure(long minute, long second, RateLimitAuthFailureStep step, int enforcedRpm)
        {
            var slot = Claim(minute);
            switch (step)
            {
                case RateLimitAuthFailureStep.Charged:
                    // Follows a Checked for the same request, so it is not a second evaluation.
                    Interlocked.Increment(ref _charged[slot]);
                    break;
                case RateLimitAuthFailureStep.Refused:
                    Interlocked.Increment(ref _evaluations[slot]);
                    Interlocked.Increment(ref _refusedByRate[slot]);
                    break;
                default:
                    Interlocked.Increment(ref _evaluations[slot]);
                    break;
            }

            if (enforcedRpm > 0)
            {
                Volatile.Write(ref _configuredRpm, enforcedRpm);
                Volatile.Write(ref _effectiveRpm, enforcedRpm);
            }

            Volatile.Write(ref _lastSecond, second);
        }

        public (long Evaluations, long Charged, long RefusedByRate, long StreamsStarted, long RefusedByStreams, long PeakCharged, long PeakMinute) Sum(
            long oldest,
            long newest)
        {
            long evaluations = 0, charged = 0, rate = 0, started = 0, streams = 0, peak = 0, peakMinute = 0;
            lock (_sync)
            {
                for (var i = 0; i < WindowMinutes; i++)
                {
                    if (_minutes[i] < oldest || _minutes[i] > newest)
                    {
                        continue;
                    }

                    evaluations += _evaluations[i];
                    charged += _charged[i];
                    rate += _refusedByRate[i];
                    started += _streamsStarted[i];
                    streams += _refusedByStreams[i];
                    if (_charged[i] > peak)
                    {
                        peak = _charged[i];
                        peakMinute = _minutes[i];
                    }
                }
            }

            return (evaluations, charged, rate, started, streams, peak, peakMinute);
        }

        public void Fill(long oldest, MinuteSample[] into)
        {
            lock (_sync)
            {
                for (var i = 0; i < WindowMinutes; i++)
                {
                    var index = _minutes[i] - oldest;
                    if (index >= 0 && index < into.Length)
                    {
                        into[index] = new MinuteSample(_evaluations[i], _charged[i], _refusedByRate[i], _refusedByStreams[i]);
                    }
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                Array.Clear(_minutes);
                Array.Clear(_evaluations);
                Array.Clear(_charged);
                Array.Clear(_refusedByRate);
                Array.Clear(_streamsStarted);
                Array.Clear(_refusedByStreams);
            }

            Volatile.Write(ref _configuredRpm, 0);
            Volatile.Write(ref _effectiveRpm, 0);
            Volatile.Write(ref _lastSecond, 0);
        }

        private int Claim(long minute)
        {
            var slot = (int)(((minute % WindowMinutes) + WindowMinutes) % WindowMinutes);
            if (Volatile.Read(ref _minutes[slot]) == minute)
            {
                return slot;
            }

            lock (_sync)
            {
                if (_minutes[slot] != minute)
                {
                    // The slot belonged to a minute a full window ago. Cleared before the minute is
                    // published, so nobody on the fast path adds to a count that is about to go.
                    Volatile.Write(ref _evaluations[slot], 0);
                    Volatile.Write(ref _charged[slot], 0);
                    Volatile.Write(ref _refusedByRate[slot], 0);
                    Volatile.Write(ref _streamsStarted[slot], 0);
                    Volatile.Write(ref _refusedByStreams[slot], 0);
                    Volatile.Write(ref _minutes[slot], minute);
                }
            }

            return slot;
        }
    }

    private sealed class Counter
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);
    }

    /// <summary>One bounded set of per-minute admission counters, keyed by whatever the section counts.</summary>
    private sealed class UsageDimension(int maxKeys)
    {
        private readonly ConcurrentDictionary<string, Ring> _keys = new(StringComparer.Ordinal);
        private readonly DropCounter _drops = new();

        public bool IsEmpty => _keys.IsEmpty;

        public RateLimitTrackerDimension Describe(string name) => _drops.Describe(name, _keys.Count, maxKeys);

        public void Add(
            string key,
            DateTimeOffset now,
            long minute,
            bool admitted,
            bool concurrencyRejection,
            int configuredRpm,
            int effectiveRpm)
        {
            if (!_keys.TryGetValue(key, out var ring))
            {
                if (_keys.Count >= maxKeys)
                {
                    _drops.Record(now);
                    return;
                }

                ring = _keys.GetOrAdd(key, static _ => new Ring());
            }

            ring.Add(minute, admitted, concurrencyRejection, configuredRpm, effectiveRpm);
        }

        public static RateLimitUsageTotals ToTotals((long Requests, long Admitted, long ConcurrencyRejected) sum)
        {
            // The remainder is by construction the token-bucket refusals: those are the only other
            // way a decision here can be a refusal.
            var rejected = sum.Requests - sum.Admitted;
            return new RateLimitUsageTotals(
                sum.Requests,
                sum.Admitted,
                rejected,
                rejected - sum.ConcurrencyRejected,
                sum.ConcurrencyRejected);
        }

        public IReadOnlyList<RateLimitUsageRow> Top(
            long oldest,
            long newest,
            int take,
            int windowMinutes,
            Func<string, (string? Tenant, string? ApiKey, string? Model)> split)
        {
            var rows = new List<RateLimitUsageRow>(Math.Min(take * 2, _keys.Count + 1));

            foreach (var (key, ring) in _keys)
            {
                var sum = ring.Sum(oldest, newest);
                if (sum.Requests == 0)
                {
                    continue;
                }

                var (tenant, apiKey, model) = split(key);
                rows.Add(new RateLimitUsageRow(
                    key,
                    tenant,
                    apiKey,
                    model,
                    sum.Requests,
                    sum.Admitted,
                    sum.Requests - sum.Admitted,
                    (double)sum.Requests / windowMinutes,
                    ring.ConfiguredRpm,
                    ring.EffectiveRpm));
            }

            rows.Sort(static (a, b) =>
                b.Requests != a.Requests
                    ? b.Requests.CompareTo(a.Requests)
                    : string.CompareOrdinal(a.Key, b.Key));

            if (rows.Count > take)
            {
                rows.RemoveRange(take, rows.Count - take);
            }

            return rows;
        }

        public void Clear()
        {
            _keys.Clear();
            _drops.Clear();
        }

        private sealed class Ring
        {
            private readonly long[] _minutes = new long[WindowMinutes];
            private readonly long[] _requests = new long[WindowMinutes];
            private readonly long[] _admitted = new long[WindowMinutes];
            private readonly long[] _concurrencyRejected = new long[WindowMinutes];
            private readonly object _sync = new();

            private int _configuredRpm;
            private int _effectiveRpm;

            /// <summary>The tier this key was last enforced against, for the "usage against limit" column.</summary>
            public int ConfiguredRpm => Volatile.Read(ref _configuredRpm);

            public int EffectiveRpm => Volatile.Read(ref _effectiveRpm);

            public void Add(
                long minute,
                bool admitted,
                bool concurrencyRejection,
                int configuredRpm,
                int effectiveRpm)
            {
                var slot = (int)(((minute % WindowMinutes) + WindowMinutes) % WindowMinutes);
                lock (_sync)
                {
                    if (_minutes[slot] != minute)
                    {
                        // The slot belonged to a minute a full window ago; reset rather than add, or
                        // the ring would accumulate counts from three hours back forever.
                        _minutes[slot] = minute;
                        _requests[slot] = 0;
                        _admitted[slot] = 0;
                        _concurrencyRejected[slot] = 0;
                    }

                    _requests[slot]++;
                    if (admitted)
                    {
                        _admitted[slot]++;
                    }
                    else if (concurrencyRejection)
                    {
                        _concurrencyRejected[slot]++;
                    }
                }

                // Last writer wins, and only a decision that actually carries a rate writes at all.
                // A concurrency decision reports zero rpm — it was made against a slot count — and
                // letting that through would blank the key's "usage against limit" columns until
                // the next rate decision happened to restore them.
                if (effectiveRpm > 0)
                {
                    Volatile.Write(ref _configuredRpm, configuredRpm);
                    Volatile.Write(ref _effectiveRpm, effectiveRpm);
                }
            }

            public (long Requests, long Admitted, long ConcurrencyRejected) Sum(long oldest, long newest)
            {
                long requests = 0, admitted = 0, concurrencyRejected = 0;
                lock (_sync)
                {
                    for (var i = 0; i < WindowMinutes; i++)
                    {
                        if (_minutes[i] >= oldest && _minutes[i] <= newest)
                        {
                            requests += _requests[i];
                            admitted += _admitted[i];
                            concurrencyRejected += _concurrencyRejected[i];
                        }
                    }
                }

                return (requests, admitted, concurrencyRejected);
            }
        }
    }
}
