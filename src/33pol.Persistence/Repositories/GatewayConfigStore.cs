using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class GatewayConfigStore(
    GatewayDbContext dbContext,
    ILogger<GatewayConfigStore>? logger = null) : IGatewayConfigStore
{
    private readonly ILogger<GatewayConfigStore> _logger = logger ?? NullLogger<GatewayConfigStore>.Instance;

    // The config sections are singleton rows; the fixed keys keep reads and bumps a pure upsert.
    private const int ConfigVersionRowId = 1;
    private const int CorsSettingsRowId = 1;
    private const int RateLimitDefaultsRowId = 1;
    private const int QuotaSettingsRowId = 1;

    public async Task<GatewayConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);

        var cors = await dbContext.CorsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == CorsSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        var rateLimits = await LoadRateLimitsAsync(cancellationToken).ConfigureAwait(false);

        var quota = await dbContext.QuotaSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == QuotaSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        return new GatewayConfigSnapshot
        {
            Version = version,
            Cors = new CorsConfigSection
            {
                AllowedOrigins = cors?.AllowedOrigins ?? [],
            },
            RateLimits = rateLimits,
            Quota = quota is null
                ? QuotaConfigSection.Defaults
                : new QuotaConfigSection
                {
                    DefaultMonthlyTokenLimit = quota.DefaultMonthlyTokenLimit,
                    SoftLimitRatio = (double)quota.SoftLimitRatio,
                },
        };
    }

    private async Task<RateLimitsConfigSection> LoadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var defaults = await dbContext.RateLimitDefaults
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == RateLimitDefaultsRowId, cancellationToken)
            .ConfigureAwait(false);

        var plans = await dbContext.RateLimitPlans
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rules = await dbContext.RateLimitRules
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // One pass over the rule table, bucketed by scope. Rules whose scope this build does not
        // recognise are ignored rather than guessed at: a row written by a newer version must not be
        // reinterpreted as some other scope's limit.
        var byScope = new Dictionary<string, Dictionary<string, RateLimitPolicy>>(StringComparer.OrdinalIgnoreCase);
        var schedules = new Dictionary<string, IReadOnlyList<RateLimitWindowDefinition>>(StringComparer.OrdinalIgnoreCase);
        var disabled = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var policy = new RateLimitPolicy(rule.Rpm, rule.Burst, rule.MaxConcurrentStreams);
            var identity = RateLimitScheduleProjection.Identity(rule.Scope, rule.TargetKey);

            // A switched-off rule is kept out of the scope maps entirely, because presence in them is
            // what enforcement means. Its tier travels in the side-car so the admin surface can still
            // list it and switch it back on; its windows are read below either way, so pausing a rule
            // and resuming it does not cost the schedule.
            if (rule.Enabled)
            {
                if (!byScope.TryGetValue(rule.Scope, out var map))
                {
                    map = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);
                    byScope[rule.Scope] = map;
                }

                map[rule.TargetKey] = policy;
            }
            else
            {
                disabled[identity] = policy;
            }

            // A schedule that cannot be read applies the base tier, never nothing: the rule stays
            // in force at its configured numbers and the windows are simply absent until fixed.
            // Logged, because from the outside "the windows vanished" is indistinguishable from a
            // deliberate change and an operator needs the pointer to the row.
            if (!RateLimitScheduleJson.TryDeserialize(rule.ScheduleJson, out var windows))
            {
                _logger.LogWarning(
                    "Rate-limit rule {Scope}:{Target} has a schedule that cannot be read; its windows are ignored and the base tier applies until the schedule is saved again.",
                    rule.Scope,
                    rule.TargetKey);
            }
            else if (windows.Count > 0)
            {
                schedules[identity] = windows;
            }
        }

        return new RateLimitsConfigSection
        {
            // No defaults row (database-less or pre-seed) means enforce, matching prior behavior.
            Enabled = defaults?.Enabled ?? true,
            AdaptiveEnabled = defaults?.AdaptiveEnabled ?? false,
            Default = defaults is null
                ? RateLimitPolicy.Default
                : new RateLimitPolicy(defaults.Rpm, defaults.Burst, defaults.MaxConcurrentStreams),
            Plans = plans.ToDictionary(
                p => p.Slug,
                p => new RateLimitPolicy(p.Rpm, p.Burst, p.MaxConcurrentStreams),
                StringComparer.OrdinalIgnoreCase),
            TenantOverrides = Scope(byScope, RateLimitScopeNames.Tenant),
            Global = Single(byScope, RateLimitScopeNames.Global),
            Models = Scope(byScope, RateLimitScopeNames.Model),
            ApiKeys = Scope(byScope, RateLimitScopeNames.ApiKey),
            TenantModels = Scope(byScope, RateLimitScopeNames.TenantModel),
            ApiKeyModels = Scope(byScope, RateLimitScopeNames.ApiKeyModel),
            AuthFailure = Single(byScope, RateLimitScopeNames.AuthFailure),
            Anonymous = Single(byScope, RateLimitScopeNames.Anonymous),
            Schedules = schedules,
            DisabledRules = disabled,
        };
    }

    private static IReadOnlyDictionary<string, RateLimitPolicy> Scope(
        Dictionary<string, Dictionary<string, RateLimitPolicy>> byScope,
        string scope) =>
        byScope.TryGetValue(scope, out var map)
            ? map
            : new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The single rule for a scope that has exactly one partition, stored under the
    /// <see cref="RateLimitScopeNames.SingletonTarget"/> key.
    /// </summary>
    private static RateLimitPolicy Single(
        Dictionary<string, Dictionary<string, RateLimitPolicy>> byScope,
        string scope) =>
        byScope.TryGetValue(scope, out var map) &&
        map.TryGetValue(RateLimitScopeNames.SingletonTarget, out var policy)
            ? policy
            : RateLimitPolicy.Unlimited;

    public async Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        return row?.Version ?? 0;
    }

    public async Task<long> IncrementVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ConfigVersionEntity { Id = ConfigVersionRowId, Version = 0 };
            dbContext.ConfigVersions.Add(row);
        }

        row.Version += 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Version;
    }
}
