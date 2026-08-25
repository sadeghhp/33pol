using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models.Overview;

namespace Pol33.Observability.Attention;

/// <summary>
/// Evaluates the Overview's attention rules. Pure over its inputs except for hysteresis: a condition
/// is remembered from the first evaluation that saw it and only listed once it has held for the
/// rule's <c>for</c> duration — the same shape as the Prometheus rules it mirrors, so a single bad
/// second does not light the banner. Cleared conditions forget their start immediately.
/// </summary>
public sealed class AttentionEvaluator(IOptions<GatewayOptions>? options = null) : IAttentionEvaluator
{
    private readonly OverviewAttentionOptions _options = options?.Value.Overview.Attention ?? new OverviewAttentionOptions();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);

    // Observation-based rates for cumulative counters the gateway cannot window itself.
    private (long Value, DateTimeOffset At)? _lastDropped;
    private DateTimeOffset? _lastDropIncreaseAt;
    private readonly Queue<(long Value, DateTimeOffset At)> _parseFailureSamples = new();
    private readonly object _sampleSync = new();

    public IReadOnlyList<AttentionItem> Evaluate(AttentionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!_options.Enabled)
        {
            return [];
        }

        var now = inputs.Now;
        var active = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        EvaluateTraffic(inputs, active);
        EvaluateBackends(inputs, active);
        EvaluatePipeline(inputs, now, active);
        EvaluateFinOps(inputs, now, active);
        EvaluatePolicy(inputs, active);
        EvaluateControlPlane(inputs, now, active);
        EvaluateTenants(inputs, active);

        // Hysteresis: remember first sight, forget anything that cleared.
        foreach (var key in _firstSeen.Keys)
        {
            if (!active.ContainsKey(key))
            {
                _firstSeen.TryRemove(key, out _);
            }
        }

        var items = new List<AttentionItem>();
        foreach (var (key, candidate) in active)
        {
            var since = _firstSeen.GetOrAdd(key, now);
            if (now - since < candidate.For)
            {
                continue;
            }

            items.Add(candidate.Item with { SinceUtc = since });
        }

        items.Sort(static (a, b) =>
        {
            var bySeverity = Rank(a.Severity).CompareTo(Rank(b.Severity));
            return bySeverity != 0 ? bySeverity : a.SinceUtc.CompareTo(b.SinceUtc);
        });
        return items;
    }

    /// <summary>Invariant "92%" / "9.5%" so the wording does not depend on the server's culture.</summary>
    private static string Pct(double ratio, int decimals = 0) =>
        (ratio * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";

    private static int Rank(string severity) => severity switch
    {
        AttentionItem.SeverityCritical => 0,
        AttentionItem.SeverityWarning => 1,
        _ => 2,
    };

    private readonly record struct Candidate(AttentionItem Item, TimeSpan For);

    private static void Add(Dictionary<string, Candidate> active, string key, AttentionItem item, TimeSpan @for) =>
        active[key] = new Candidate(item, @for);

    private static AttentionLink Link(string tab, params (string Key, string Value)[] parameters) =>
        new(tab, parameters.Length == 0 ? null : parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private void EvaluateTraffic(AttentionInputs inputs, Dictionary<string, Candidate> active)
    {
        var five = inputs.Windows?.FirstOrDefault(w => w.Window == "5m");
        if (five is null || five.Requests < _options.ErrorRateMinRequests)
        {
            return;
        }

        if (five.ErrorRate > _options.ErrorRateWarn)
        {
            Add(active, "error_rate_high", new AttentionItem
            {
                Severity = AttentionItem.SeverityWarning,
                Code = "error_rate_high",
                Title = $"Error rate {Pct(five.ErrorRate, 1)} over the last 5 minutes",
                Detail = $"{five.Errors:N0} of {five.Requests:N0} requests failed; the threshold is {Pct(_options.ErrorRateWarn)}.",
                Link = Link("errors", ("range", "1h")),
            }, TimeSpan.FromSeconds(_options.ErrorRateForSeconds));
        }
    }

    private void EvaluateBackends(AttentionInputs inputs, Dictionary<string, Candidate> active)
    {
        var backends = inputs.Backends;
        if (backends is null || backends.Count == 0)
        {
            return;
        }

        if (backends.All(b => !b.IsHealthy))
        {
            Add(active, "no_healthy_backends", new AttentionItem
            {
                Severity = AttentionItem.SeverityCritical,
                Code = "no_healthy_backends",
                Title = "No healthy backends",
                Detail = $"All {backends.Count} registered models failed their last health probe; every inference request is being refused.",
                Link = Link("routing", ("sub", "backends")),
            }, TimeSpan.FromSeconds(_options.BackendUnhealthyForSeconds));
        }

        foreach (var b in backends)
        {
            if (!b.IsHealthy)
            {
                Add(active, "backend_unhealthy:" + b.ModelId, new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "backend_unhealthy",
                    Title = $"{b.ModelId} is unhealthy",
                    Detail = string.IsNullOrEmpty(b.Error) ? $"Last probe of {b.Url} failed." : $"{b.Error} ({b.Url})",
                    ModelId = b.ModelId,
                    Link = Link("routing", ("sub", "backends"), ("model", b.ModelId)),
                }, TimeSpan.FromSeconds(_options.BackendUnhealthyForSeconds));
            }

            if (b.CircuitState == "open")
            {
                Add(active, "circuit_open:" + b.ModelId, new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "circuit_open",
                    Title = $"Circuit open for {b.ModelId}",
                    Detail = "Requests are being rejected until a probe succeeds; the upstream failed repeatedly.",
                    ModelId = b.ModelId,
                    Link = Link("routing", ("sub", "backends"), ("model", b.ModelId)),
                }, TimeSpan.FromSeconds(_options.CircuitOpenForSeconds));
            }

            if (b.MaxConcurrent > 0 && b.InFlight >= b.MaxConcurrent && (b.Queued > 0 || b.MaxQueued == 0))
            {
                Add(active, "bulkhead_saturated:" + b.ModelId, new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "bulkhead_saturated",
                    Title = $"{b.ModelId} is at its concurrency ceiling",
                    Detail = $"{b.InFlight}/{b.MaxConcurrent} slots busy, {b.Queued} queued — new requests are waiting or being refused.",
                    ModelId = b.ModelId,
                    Link = Link("settings", ("sub", "limits")),
                }, TimeSpan.FromSeconds(60));
            }
        }
    }

    private void EvaluatePipeline(AttentionInputs inputs, DateTimeOffset now, Dictionary<string, Candidate> active)
    {
        var p = inputs.Pipeline;
        if (p is null)
        {
            return;
        }

        if (p.UsageWriterQueueDepth > _options.UsageWriterQueueWarn)
        {
            Add(active, "usage_writer_backlog", new AttentionItem
            {
                Severity = AttentionItem.SeverityWarning,
                Code = "usage_writer_backlog",
                Title = "Usage writer is backing up",
                Detail = $"{p.UsageWriterQueueDepth:N0} usage events queued (capacity {p.UsageWriterCapacity:N0}); the billing database is not keeping up.",
                Link = Link("settings", ("sub", "observability")),
            }, TimeSpan.FromSeconds(300));
        }

        lock (_sampleSync)
        {
            if (_lastDropped is { } last && p.UsageWriterDropped > last.Value)
            {
                _lastDropIncreaseAt = now;
            }

            _lastDropped = (p.UsageWriterDropped, now);
            if (_lastDropIncreaseAt is { } at && now - at <= TimeSpan.FromMinutes(5))
            {
                Add(active, "usage_events_dropped", new AttentionItem
                {
                    Severity = AttentionItem.SeverityCritical,
                    Code = "usage_events_dropped",
                    Title = "Usage events are being dropped",
                    Detail = $"{p.UsageWriterDropped:N0} events never reached the billing ledger; those requests are unbilled.",
                    Link = Link("settings", ("sub", "observability")),
                }, TimeSpan.Zero);
            }

            _parseFailureSamples.Enqueue((p.UsageParseFailures, now));
            while (_parseFailureSamples.Count > 0 && now - _parseFailureSamples.Peek().At > TimeSpan.FromMinutes(5))
            {
                _parseFailureSamples.Dequeue();
            }

            if (_parseFailureSamples.Count >= 2)
            {
                var oldest = _parseFailureSamples.Peek();
                var elapsed = (now - oldest.At).TotalSeconds;
                if (elapsed > 0)
                {
                    var rate = (p.UsageParseFailures - oldest.Value) / elapsed;
                    if (rate > _options.UsageParseFailureRatePerSecondWarn)
                    {
                        Add(active, "usage_parse_failures", new AttentionItem
                        {
                            Severity = AttentionItem.SeverityWarning,
                            Code = "usage_parse_failures",
                            Title = "Upstream usage frames are not parsing",
                            Detail = $"{rate.ToString("F2", CultureInfo.InvariantCulture)} parse failures per second over the last 5 minutes; those requests are billed from estimates.",
                            Link = Link("logs"),
                        }, TimeSpan.FromSeconds(300));
                    }
                }
            }
        }
    }

    private void EvaluateFinOps(AttentionInputs inputs, DateTimeOffset now, Dictionary<string, Candidate> active)
    {
        var f = inputs.FinOps;
        if (f is null)
        {
            return;
        }

        if (f.Reconciliation is { Enabled: true } r)
        {
            if (r.DiscrepancyCount > 0)
            {
                Add(active, "reconciliation_discrepancies", new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "reconciliation_discrepancies",
                    Title = "Billing rollups disagree with the ledger",
                    Detail = $"{r.DiscrepancyCount:N0} daily buckets differ from their billing events, {r.AbsoluteCostDrift.ToString("0.####", CultureInfo.InvariantCulture)} {f.Currency} in total.",
                    Link = Link("usage"),
                }, TimeSpan.FromMinutes(15));
            }

            // A sweep that has never run since start-up is not "stalled" — the first one is delayed
            // on purpose. Only a sweep that ran and then stopped is.
            if (r.LastRunUtc is { } lastRun && now - lastRun > TimeSpan.FromMinutes(_options.ReconciliationStalledAfterMinutes))
            {
                Add(active, "reconciliation_stalled", new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "reconciliation_stalled",
                    Title = "Billing reconciliation has stalled",
                    Detail = $"Last sweep finished {r.LastRunUtc:u}; it should run hourly.",
                    Link = Link("settings", ("sub", "observability")),
                }, TimeSpan.Zero);
            }
        }

        if (f.UnpricedModelIds.Count > 0)
        {
            Add(active, "unpriced_models", new AttentionItem
            {
                Severity = AttentionItem.SeverityInfo,
                Code = "unpriced_models",
                Title = $"{f.UnpricedModelIds.Count} model{(f.UnpricedModelIds.Count == 1 ? " has" : "s have")} no rate card",
                Detail = "Their usage is recorded but costs zero: " + string.Join(", ", f.UnpricedModelIds.Take(5)) + (f.UnpricedModelIds.Count > 5 ? ", …" : "") + ".",
                Link = Link("routing", ("sub", "models")),
            }, TimeSpan.Zero);
        }

        foreach (var b in f.Budgets)
        {
            EvaluateBudget(b, active);
        }
    }

    private void EvaluateBudget(BudgetStatus b, Dictionary<string, Candidate> active)
    {
        var key = b.BudgetId.ToString("N");
        var who = string.IsNullOrEmpty(b.TenantSlug) ? b.TenantId.ToString() : b.TenantSlug;
        if (b.Ratio >= 1)
        {
            Add(active, "budget_exceeded:" + key, new AttentionItem
            {
                Severity = b.HardStopEnabled ? AttentionItem.SeverityCritical : AttentionItem.SeverityWarning,
                Code = b.HardStopEnabled ? "budget_hard_stop" : "budget_exceeded",
                Title = $"Budget \"{b.Name}\" for {who} is exhausted",
                Detail = b.HardStopEnabled
                    ? $"{b.Spent.ToString("0.##", CultureInfo.InvariantCulture)} of {b.Limit.ToString("0.##", CultureInfo.InvariantCulture)} {b.Currency} spent; the hard stop is refusing this tenant's requests."
                    : $"{b.Spent.ToString("0.##", CultureInfo.InvariantCulture)} of {b.Limit.ToString("0.##", CultureInfo.InvariantCulture)} {b.Currency} spent; no hard stop, so spend continues.",
                TenantId = b.TenantId.ToString(),
                Link = Link("usage"),
            }, TimeSpan.Zero);
            return;
        }

        var threshold = Math.Min(b.WarningRatio <= 0 ? 1 : b.WarningRatio, _options.BudgetNearLimitRatio);
        if (b.Ratio >= threshold)
        {
            var breach = b.ProjectedBreachDate is { } d ? $" At the current rate it runs out on {d.ToString("MMM d", CultureInfo.InvariantCulture)}." : string.Empty;
            Add(active, "budget_near_limit:" + key, new AttentionItem
            {
                Severity = AttentionItem.SeverityWarning,
                Code = "budget_near_limit",
                Title = $"Budget \"{b.Name}\" for {who} at {Pct(b.Ratio)}",
                Detail = $"{b.Spent.ToString("0.##", CultureInfo.InvariantCulture)} of {b.Limit.ToString("0.##", CultureInfo.InvariantCulture)} {b.Currency} spent this period.{breach}",
                TenantId = b.TenantId.ToString(),
                Link = Link("usage"),
            }, TimeSpan.Zero);
        }
    }

    private static void EvaluatePolicy(AttentionInputs inputs, Dictionary<string, Candidate> active)
    {
        var p = inputs.Policy;
        if (p is null)
        {
            return;
        }

        foreach (var q in p.Quotas)
        {
            if (!q.NearLimit && !q.Exceeded)
            {
                continue;
            }

            var who = string.IsNullOrEmpty(q.TenantSlug) ? q.PartitionKey : q.TenantSlug;
            Add(active, "quota_near_limit:" + q.PartitionKey, new AttentionItem
            {
                Severity = q.Exceeded ? AttentionItem.SeverityWarning : AttentionItem.SeverityInfo,
                Code = q.Exceeded ? "quota_exceeded" : "quota_near_limit",
                Title = q.Exceeded ? $"{who} has used its monthly token quota" : $"{who} is at {Pct(q.Ratio)} of its monthly token quota",
                Detail = $"{q.Used:N0} of {q.Limit:N0} tokens in {q.Period}.",
                TenantId = q.PartitionKey,
                Link = Link("settings", ("sub", "limits")),
            }, TimeSpan.Zero);
        }
    }

    private void EvaluateControlPlane(AttentionInputs inputs, DateTimeOffset now, Dictionary<string, Candidate> active)
    {
        var c = inputs.ControlPlane;
        if (c is null)
        {
            return;
        }

        if (c.Secrets.Undecryptable > 0)
        {
            Add(active, "secrets_undecryptable", new AttentionItem
            {
                Severity = AttentionItem.SeverityCritical,
                Code = "secrets_undecryptable",
                Title = $"{c.Secrets.Undecryptable} upstream credential{(c.Secrets.Undecryptable == 1 ? string.Empty : "s")} cannot be decrypted",
                Detail = "Gateway:Security:KeyPepper differs from the one the secrets were written with; those models will fail upstream auth.",
                Link = Link("settings", ("sub", "runtime")),
            }, TimeSpan.Zero);
        }

        if (c.Database.Configured)
        {
            if (c.LastBackup is null)
            {
                Add(active, "backup_stale", new AttentionItem
                {
                    Severity = AttentionItem.SeverityInfo,
                    Code = "backup_stale",
                    Title = "No database backup has been taken",
                    Detail = "POST /admin/api/maintenance/backup produces a verified copy next to the live file.",
                    Link = Link("settings", ("sub", "runtime")),
                }, TimeSpan.Zero);
            }
            else if (!c.LastBackup.Succeeded)
            {
                Add(active, "backup_failed", new AttentionItem
                {
                    Severity = AttentionItem.SeverityWarning,
                    Code = "backup_failed",
                    Title = "The last database backup failed",
                    Detail = c.LastBackup.Error ?? $"Integrity check: {c.LastBackup.IntegrityCheck}.",
                    Link = Link("settings", ("sub", "runtime")),
                }, TimeSpan.Zero);
            }
            else if (now - c.LastBackup.AttemptedAtUtc > TimeSpan.FromDays(_options.BackupStaleAfterDays))
            {
                Add(active, "backup_stale", new AttentionItem
                {
                    Severity = AttentionItem.SeverityInfo,
                    Code = "backup_stale",
                    Title = $"Last database backup is {(now - c.LastBackup.AttemptedAtUtc).TotalDays:F0} days old",
                    Detail = $"Taken {c.LastBackup.AttemptedAtUtc:u}.",
                    Link = Link("settings", ("sub", "runtime")),
                }, TimeSpan.Zero);
            }
        }
    }

    private void EvaluateTenants(AttentionInputs inputs, Dictionary<string, Candidate> active)
    {
        var t = inputs.Tenants;
        if (t is null)
        {
            return;
        }

        if (t.ExpiringKeys.Count > 0)
        {
            Add(active, "key_expiring", new AttentionItem
            {
                Severity = AttentionItem.SeverityInfo,
                Code = "key_expiring",
                Title = $"{t.ExpiringKeys.Count} API key{(t.ExpiringKeys.Count == 1 ? " expires" : "s expire")} within {_options.KeyExpiringWithinDays} days",
                Detail = string.Join(", ", t.ExpiringKeys.Take(5).Select(k => k.Label ?? k.KeyPrefix)) + (t.ExpiringKeys.Count > 5 ? ", …" : string.Empty),
                Link = Link("keys"),
            }, TimeSpan.Zero);
        }

        if (t.IdleKeys.Count > 0)
        {
            Add(active, "key_idle", new AttentionItem
            {
                Severity = AttentionItem.SeverityInfo,
                Code = "key_idle",
                Title = $"{t.IdleKeys.Count} API key{(t.IdleKeys.Count == 1 ? " has" : "s have")} been idle for {_options.KeyIdleAfterDays}+ days",
                Detail = "Unused credentials are attack surface; revoke the ones nobody needs.",
                Link = Link("keys"),
            }, TimeSpan.Zero);
        }
    }
}
