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

        if (!string.IsNullOrEmpty(tenant))
        {
            _byTenant.Add(tenant, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);

            if (!string.IsNullOrEmpty(model))
            {
                _byTenantModel.Add(
                    RateLimitKeys.Pair(tenant, model),
                    minute,
                    admitted,
                    concurrencyRejection,
                    usageEvent.ConfiguredRpm,
                    usageEvent.EffectiveRpm);
            }
        }

        if (!string.IsNullOrEmpty(model))
        {
            _byModel.Add(model, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            _byApiKey.Add(apiKey, minute, admitted, concurrencyRejection, usageEvent.ConfiguredRpm, usageEvent.EffectiveRpm);
        }

        if (!admitted && usageEvent.Scope is { } scope)
        {
            RecordViolation(scope, usageEvent.Control, tenant, apiKey, model);
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
        string? model)
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

        var totals = _byTenant.IsEmpty
            ? _byModel.Totals(oldest, newest)
            : _byTenant.Totals(oldest, newest);

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
            BuildStoreReport());
    }

    public void Reset()
    {
        _byTenantModel.Clear();
        _byTenant.Clear();
        _byModel.Clear();
        _byApiKey.Clear();
        _violations.Clear();
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

        public bool IsEmpty => _keys.IsEmpty;

        public void Add(
            string key,
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
                    return;
                }

                ring = _keys.GetOrAdd(key, static _ => new Ring());
            }

            ring.Add(minute, admitted, concurrencyRejection, configuredRpm, effectiveRpm);
        }

        public RateLimitUsageTotals Totals(long oldest, long newest)
        {
            long requests = 0, admitted = 0, concurrencyRejected = 0;
            foreach (var ring in _keys.Values)
            {
                var sum = ring.Sum(oldest, newest);
                requests += sum.Requests;
                admitted += sum.Admitted;
                concurrencyRejected += sum.ConcurrencyRejected;
            }

            // Counted from one dimension only — the caller picks a single one — so a request
            // refused once is counted once, however many sections it also appears under. The
            // remainder is by construction the token-bucket refusals: those are the only other way
            // a decision here can be a refusal.
            var rejected = requests - admitted;
            return new RateLimitUsageTotals(
                requests,
                admitted,
                rejected,
                rejected - concurrencyRejected,
                concurrencyRejected);
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

        public void Clear() => _keys.Clear();

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
