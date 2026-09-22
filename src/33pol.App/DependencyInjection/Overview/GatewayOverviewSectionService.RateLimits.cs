using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models.Overview;
using Pol33.Core.RateLimiting;
using Pol33.Observability.RateLimiting;
using Pol33.Proxy.Routing;

namespace Pol33.App.DependencyInjection.Overview;

internal sealed partial class GatewayOverviewSectionService
{
    /// <summary>Row cap for the subject dimensions; the tracker holds 500 keys per dimension by default.</summary>
    private const int RateLimitSubjectTake = 1000;

    private const int RateLimitTopSubjects = 5;

    private const int RateLimitTopLimits = 6;

    // ---- Rate limits ----

    /// <summary>
    /// Everything here is a read of the usage tracker, the stored configuration and the schedule
    /// report; nothing is recomputed. The per-subject <c>utilization</c> is deliberately not used —
    /// it is whichever scope was tightest on that subject's latest request, not a measurement.
    /// </summary>
    private async Task<RateLimitOverview?> BuildRateLimitsAsync(CancellationToken cancellationToken)
    {
        if (rateLimitTracker is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hour = rateLimitTracker.BuildReport(60, RateLimitSubjectTake, now);
        var five = rateLimitTracker.BuildReport(5, 1, now);

        var stored = rateLimitAdmin?.GetCurrent();
        var rules = stored?.Rules
            .Where(static r => r.Scope is not RateLimitScopeNames.AuthFailure and not RateLimitScopeNames.Anonymous)
            .ToArray() ?? [];

        var refusedTenants = hour.ByTenant.Where(static r => r.Rejected > 0).OrderByDescending(static r => r.Rejected).ToArray();
        var refusedKeys = hour.ByApiKey.Where(static r => r.Rejected > 0).OrderByDescending(static r => r.Rejected).ToArray();

        var limits = hour.Limits
            .Select(static row => new RateLimitLimitOverview(
                row.LimitId,
                RateLimitLimitIds.HasSingleBucket(row.Scope) ? row.LimitId : null,
                row.Scope,
                row.Target,
                row.AnonymousBucket,
                row.SingleBucket,
                row.Evaluations,
                row.RefusedByRate + row.RefusedByStreams,
                row.RefusedByRate,
                row.RefusedByStreams,
                row.ConfiguredRpm,
                row.EffectiveRpm,
                // Only a single bucket has a peak comparable with its rate; never pass one through otherwise.
                row.SingleBucket ? row.PeakUtilization : null,
                row.LastDecisionUtc))
            .ToArray();
        var topLimits = limits
            .Where(static l => l.Refused > 0 || l.PeakUtilization >= RateLimitOverviewLimits.NearRatio)
            .OrderByDescending(static l => l.Refused)
            .ThenByDescending(static l => l.PeakUtilization ?? -1)
            .ThenByDescending(static l => l.Evaluations)
            .ThenBy(static l => l.LimitId, StringComparer.Ordinal)
            .Take(RateLimitTopLimits)
            .ToArray();

        // One lookup for every label shown: refused subjects, and the keys that key-scoped limits name.
        var (tenantLabels, keyLabels) = await ResolveRateLimitSubjectsAsync(
            refusedTenants.Take(RateLimitTopSubjects).Select(static r => r.Key),
            refusedKeys.Take(RateLimitTopSubjects).Select(static r => r.Key)
                .Concat(topLimits.Select(static l => KeyOfLimit(l)).OfType<string>()),
            cancellationToken).ConfigureAwait(false);

        var adaptiveReduced = hour.Adaptive.Models.Where(static m => m.Factor < 1).OrderBy(static m => m.Factor).ToArray();
        var store = hour.Store;
        var dims = hour.Tracker.Dimensions;

        return new RateLimitOverview
        {
            BuiltAtUtc = now,
            Enforced = configProvider?.Current.RateLimits.Enabled ?? stored?.Enabled ?? true,
            AdaptiveEnabled = configProvider?.Current.RateLimits.AdaptiveEnabled ?? stored?.AdaptiveEnabled ?? false,
            RuleCount = rules.Length,
            DisabledRuleCount = rules.Count(static r => !r.Enabled),
            ConfigReloadInProgress = configReload?.IsReloadInProgress ?? false,
            Schedule = BuildRateLimitSchedule(rules, now),
            Retention = new RateLimitRetentionOverview(RateLimitUsageTracker.WindowMinutes, hour.Tracker.TrackingSinceUtc),
            LastHour = RefusalWindow(hour),
            LastFiveMinutes = RefusalWindow(five),
            RefusedTenantCount = refusedTenants.Length,
            RefusedKeyCount = refusedKeys.Length,
            RefusedSubjectsTruncated = hour.ByTenant.Count >= RateLimitSubjectTake || hour.ByApiKey.Count >= RateLimitSubjectTake,
            TopRefusedTenants = TenantRows(refusedTenants.Take(RateLimitTopSubjects), tenantLabels),
            TopRefusedKeys = refusedKeys.Take(RateLimitTopSubjects).Select(r => Key(r, keyLabels)).ToArray(),
            RefusingLimitCount = limits.Count(static l => l.Refused > 0),
            Limits = topLimits.Select(l => l with { TargetLabel = TargetLabel(l, keyLabels) }).ToArray(),
            Protective = hour.Protective
                .Select(static p => new RateLimitProtectiveOverview(p.Scope, p.Checked, p.Refused + p.RefusedByStreams, p.EnforcedRpm, p.LastDecisionUtc))
                .ToArray(),
            Adaptive = new RateLimitAdaptiveOverview(
                hour.Adaptive.Enabled,
                adaptiveReduced.Length,
                adaptiveReduced.Length > 0 ? adaptiveReduced[0].Factor : null,
                adaptiveReduced.Length > 0 ? adaptiveReduced[0].ModelId : null,
                hour.Adaptive.BackedOffPartitions,
                hour.Adaptive.LastEvaluatedUtc),
            Tracker = new RateLimitTrackerOverview(
                hour.Tracker.IsSaturated,
                dims.Sum(static d => d.DroppedDecisions),
                dims.Where(static d => d.FirstDroppedUtc is not null).Select(static d => d.FirstDroppedUtc).Min(),
                hour.Tracker.MaxKeysPerDimension)
            {
                AtCapacity = dims.Where(static d => d.AtCapacity).Select(static d => d.Name).ToArray(),
            },
            Store = new RateLimitStoreOverview(
                store.RequestPartitions,
                store.StreamPartitions,
                store.MaxPartitions,
                // max(partitions{dimension!="ceiling"}) / clamp_min(ceiling, 1) — the Prometheus rule's own expression.
                (double)Math.Max(store.RequestPartitions, store.StreamPartitions) / Math.Max(store.MaxPartitions, 1)),
        };
    }

    private static RateLimitRefusalWindow RefusalWindow(RateLimitUsageReport report) => new(
        report.WindowMinutes,
        report.Totals.Requests,
        report.Totals.Admitted,
        report.Totals.Rejected,
        report.Totals.RateRejected,
        report.Totals.ConcurrencyRejected,
        report.Totals.RejectionRate);

    /// <summary>
    /// Windows in force and the next change, from the schedule report the rate-limits calendar reads.
    /// A zero-length range: only the per-rule status is wanted, which does not depend on the range.
    /// </summary>
    private RateLimitScheduleOverview BuildRateLimitSchedule(IReadOnlyList<RateLimitRuleDefinition> rules, DateTimeOffset now)
    {
        if (rateLimitAdmin is null)
        {
            return RateLimitScheduleOverview.Unavailable;
        }

        RateLimitScheduleReport report;
        try
        {
            report = rateLimitAdmin.GetSchedule(now, now, now, take: 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Reading the rate-limit schedule for the Overview failed");
            return RateLimitScheduleOverview.Unavailable;
        }

        ScheduleRuleStatus? next = null;
        foreach (var status in report.Rules)
        {
            if (status.NextChangeAt is { } at && at > now && (next?.NextChangeAt is not { } best || at < best))
            {
                next = status;
            }
        }

        return new RateLimitScheduleOverview(
            Available: true,
            ScheduledRuleCount: rules.Count(static r => r.Enabled && r.HasWindows),
            WindowsActiveNow: report.Rules.Count(static r => r.ActiveWindow is not null),
            NextChangeAtUtc: next?.NextChangeAt,
            NextChangeRuleId: next is null ? null : RateLimitLimitIds.Rule(next.Scope, next.Target),
            NextChangeWindow: next?.NextWindow);
    }

    /// <summary>
    /// Tenant slugs and key labels for the listed subjects only. Labels are a convenience: without a
    /// database, or if the lookup fails, the rows keep their ids and the section is still served.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, string> Tenants, IReadOnlyDictionary<Guid, (string Label, string? TenantSlug)> Keys)> ResolveRateLimitSubjectsAsync(
        IEnumerable<string> tenantKeys,
        IEnumerable<string> apiKeyIds,
        CancellationToken cancellationToken)
    {
        var tenantIds = tenantKeys.Select(ParseGuid).OfType<Guid>().ToArray();
        var keyIds = apiKeyIds.Select(ParseGuid).OfType<Guid>().ToArray();
        var noTenants = new Dictionary<Guid, string>();
        var noKeys = new Dictionary<Guid, (string, string?)>();
        if (tenantIds.Length == 0 && keyIds.Length == 0)
        {
            return (noTenants, noKeys);
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var tenantRepo = scope.ServiceProvider.GetService<ITenantRepository>();
            var keyRepo = scope.ServiceProvider.GetService<IApiKeyRepository>();
            var slugs = tenantRepo is null
                ? noTenants
                : (await tenantRepo.ListActiveAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(t => t.Id, t => t.Slug);

            var keys = noKeys;
            if (keyRepo is not null && keyIds.Length > 0)
            {
                keys = (await keyRepo.GetByIdsAsync(keyIds, cancellationToken).ConfigureAwait(false))
                    .ToDictionary(
                        k => k.Id,
                        // The public prefix is what the Keys page shows; the hash never leaves the repository.
                        k => (string.IsNullOrWhiteSpace(k.Label) ? k.KeyPrefix : k.Label!, slugs.TryGetValue(k.TenantId, out var s) ? (string?)s : null));
            }

            return (slugs, keys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No subject ids in the message: the section still renders, with ids in place of labels.
            logger.LogDebug(ex, "Resolving rate-limit subject labels for the Overview failed");
            return (noTenants, noKeys);
        }
    }

    private static Guid? ParseGuid(string key) => Guid.TryParse(key, out var id) ? id : null;

    /// <summary>
    /// Tenant rows. An anonymous caller's tracker key is its client address block, which this card
    /// never shows; the row is keyed <c>anonymous:1</c>, <c>anonymous:2</c>… instead so the address
    /// does not leave the gateway through this section.
    /// </summary>
    private static RateLimitRefusedSubject[] TenantRows(IEnumerable<RateLimitUsageRow> rows, IReadOnlyDictionary<Guid, string> labels)
    {
        var anonymousSeen = 0;
        var result = new List<RateLimitRefusedSubject>();
        foreach (var row in rows)
        {
            if (row.Key.StartsWith(RateLimitPartition.AnonymousPrefix, StringComparison.Ordinal))
            {
                result.Add(new RateLimitRefusedSubject("anonymous:" + ++anonymousSeen, "anonymous", null, true, row.Requests, row.Rejected));
                continue;
            }

            var label = ParseGuid(row.Key) is { } id && labels.TryGetValue(id, out var slug) ? slug : null;
            result.Add(new RateLimitRefusedSubject(row.Key, label, label, false, row.Requests, row.Rejected));
        }

        return [.. result];
    }

    /// <summary>The key a key-scoped limit names (<c>api_key:&lt;id&gt;</c>, <c>api_key_model:&lt;id&gt;|model</c>).</summary>
    private static string? KeyOfLimit(RateLimitLimitOverview l) =>
        l.Scope is RateLimitScopeNames.ApiKey or RateLimitScopeNames.ApiKeyModel ? l.Target.Split('|')[0] : null;

    /// <summary>
    /// A display name for a key-scoped limit's target: the key's label or public prefix, then the
    /// model for a pair. A key the lookup could not find is "unknown key", never its id. Other scopes
    /// already name their target readably and get null.
    /// </summary>
    private static string? TargetLabel(RateLimitLimitOverview l, IReadOnlyDictionary<Guid, (string Label, string? TenantSlug)> keys)
    {
        if (KeyOfLimit(l) is not { } key)
        {
            return null;
        }

        var name = ParseGuid(key) is { } id && keys.TryGetValue(id, out var k) ? k.Label : "unknown key";
        var bar = l.Target.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? name : name + " · " + l.Target[(bar + 1)..];
    }

    private static RateLimitRefusedSubject Key(RateLimitUsageRow row, IReadOnlyDictionary<Guid, (string Label, string? TenantSlug)> labels)
    {
        var found = ParseGuid(row.Key) is { } id && labels.TryGetValue(id, out var k) ? k : ((string Label, string? TenantSlug)?)null;
        return new RateLimitRefusedSubject(row.Key, found?.Label, found?.TenantSlug, false, row.Requests, row.Rejected);
    }
}
