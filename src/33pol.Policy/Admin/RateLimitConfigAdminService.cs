using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.Admin;

/// <summary>
/// Reads rate limits from the live config snapshot and persists updates to the database, forcing an
/// in-process snapshot refresh so a change takes effect without a restart. Requires a configured
/// database; in a DB-less deployment rate limits are read-only from appsettings.
/// </summary>
public sealed class RateLimitConfigAdminService(
    IGatewayConfigProvider configProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<RateLimitConfigAdminService> logger,
    TimeProvider? timeProvider = null) : IRateLimitConfigAdminService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The stored configuration: base tiers and schedules. The live snapshot is the projected one,
    /// so reading it directly would show an active window's tier as the configured number.
    /// </summary>
    private RateLimitsConfigSection StoredRateLimits => configProvider.Current.RateLimits.StoredOrSelf;

    public RateLimitAdminConfig GetCurrent()
    {
        var rateLimits = StoredRateLimits;
        return new RateLimitAdminConfig
        {
            Version = configProvider.Current.Version,
            Enabled = rateLimits.Enabled,
            AdaptiveEnabled = rateLimits.AdaptiveEnabled,
            Default = ToTierOptions(rateLimits.Default),
            Plans = rateLimits.Plans.ToDictionary(
                static p => p.Key,
                static p => ToTierOptions(p.Value),
                StringComparer.OrdinalIgnoreCase),
            Rules = WithSchedules(ToRules(rateLimits), rateLimits),
        };
    }

    /// <summary>
    /// Flattens the snapshot's per-scope maps back into the flat rule list the admin API speaks, in
    /// scope order so a GET is stable and diffable between calls.
    /// </summary>
    private static List<RateLimitRuleDefinition> ToRules(Core.Configuration.RateLimitsConfigSection rateLimits)
    {
        var rules = new List<RateLimitRuleDefinition>();

        if (!rateLimits.Global.EnforcesNothing)
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(
                RateLimitScopeNames.Global,
                RateLimitScopeNames.SingletonTarget,
                rateLimits.Global));
        }

        AddScope(rules, RateLimitScopeNames.Tenant, rateLimits.TenantOverrides);
        AddScope(rules, RateLimitScopeNames.ApiKey, rateLimits.ApiKeys);
        AddScope(rules, RateLimitScopeNames.Model, rateLimits.Models);
        AddScope(rules, RateLimitScopeNames.TenantModel, rateLimits.TenantModels);
        AddScope(rules, RateLimitScopeNames.ApiKeyModel, rateLimits.ApiKeyModels);

        if (!rateLimits.AuthFailure.EnforcesNothing)
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(
                RateLimitScopeNames.AuthFailure,
                RateLimitScopeNames.SingletonTarget,
                rateLimits.AuthFailure));
        }

        if (!rateLimits.Anonymous.EnforcesNothing)
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(
                RateLimitScopeNames.Anonymous,
                RateLimitScopeNames.SingletonTarget,
                rateLimits.Anonymous));
        }

        return rules;
    }

    private static void AddScope(
        List<RateLimitRuleDefinition> rules,
        string scope,
        IReadOnlyDictionary<string, RateLimitPolicy> map)
    {
        foreach (var (target, policy) in map.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(scope, target, policy));
        }
    }

    /// <summary>Each rule with its stored schedule attached; a rule without one gets an empty list, never null.</summary>
    private static List<RateLimitRuleDefinition> WithSchedules(
        List<RateLimitRuleDefinition> rules,
        Core.Configuration.RateLimitsConfigSection stored)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            stored.Schedules.TryGetValue(RateLimitScheduleProjection.Identity(rule.Scope, rule.TargetKey), out var windows);
            rules[i] = rule with { Schedule = windows ?? [] };
        }

        return rules;
    }

    public RateLimitScheduleReport GetSchedule(DateTimeOffset at, DateTimeOffset from, DateTimeOffset to, int take)
    {
        var stored = StoredRateLimits;
        var rules = WithSchedules(ToRules(stored), stored);
        return RateLimitScheduleReportBuilder.Build(rules, at, from, to, take);
    }

    public RateLimitWindowPreview PreviewWindow(RateLimitRuleDefinition rule, string candidateName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return RateLimitWindowPreviewBuilder.Build(rule, candidateName, _timeProvider.GetUtcNow());
    }

    public async Task<RateLimitConfigUpdateResult> UpdateAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        IReadOnlyList<RateLimitRuleDefinition>? rules = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        // Tier values are validated even when disabling, so re-enabling later cannot restore a
        // configuration that was never checked.
        if (!RateLimitConfigValidation.TryValidate(defaultTier, plans, out var validationError))
        {
            return RateLimitConfigUpdateResult.Fail(validationError!, statusCode: 400);
        }

        var stored = StoredRateLimits;

        // A rule whose schedule the caller left unspecified keeps the schedule stored under its
        // identity, for the same reason a null rule list keeps the stored rules: a client that
        // cannot see windows must not be able to delete them by omission.
        IReadOnlyList<RateLimitRuleDefinition>? submittedRules = rules?
            .Select(rule => rule is null || rule.Schedule is not null
                ? rule!
                : rule with
                {
                    Schedule = stored.Schedules.TryGetValue(
                        RateLimitScheduleProjection.Identity(rule.Scope, rule.TargetKey),
                        out var kept)
                        ? kept
                        : [],
                })
            .ToArray();

        if (!RateLimitConfigValidation.TryValidateRules(submittedRules, out var ruleError))
        {
            return RateLimitConfigUpdateResult.Fail(ruleError!, statusCode: 400);
        }

        // Null means "the caller does not manage rules", so the stored set is carried through
        // unchanged rather than wiped by a client that predates them.
        IReadOnlyList<RateLimitRuleDefinition> effectiveRules = submittedRules ?? WithSchedules(ToRules(stored), stored);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetService<IRateLimitSettingsRepository>();
        if (repository is null)
        {
            return RateLimitConfigUpdateResult.Fail(
                "Rate-limit updates require a configured database.",
                statusCode: 503);
        }

        try
        {
            var planPolicies = plans.ToDictionary(
                static p => p.Key,
                static p => ToPolicy(p.Value),
                StringComparer.OrdinalIgnoreCase);

            await repository
                .SaveAsync(
                    enabled,
                    adaptiveEnabled,
                    ToPolicy(defaultTier),
                    planPolicies,
                    effectiveRules,
                    expectedVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            var refresher = scope.ServiceProvider.GetService<IGatewayConfigRefresher>();
            if (refresher is not null)
            {
                await refresher.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Updated rate limits (enabled={Enabled}, adaptive={Adaptive}, default + {PlanCount} plan tier(s), {RuleCount} scoped rule(s), {WindowCount} schedule window(s)).",
                enabled,
                adaptiveEnabled,
                plans.Count,
                effectiveRules.Count,
                effectiveRules.Sum(static r => r.Windows.Count));
            return RateLimitConfigUpdateResult.Ok(
                enabled ? "Rate limits updated." : "Rate limits updated. Rate limiting is now disabled.");
        }
        catch (RateLimitVersionConflictException ex)
        {
            // Not an error to log at Error: somebody else saved first, and the caller is being told to
            // go and look. The rule set is written wholesale, so letting this through would delete
            // their change rather than merge with it.
            logger.LogInformation(
                "Rate-limit update refused: based on version {Expected}, current version is {Actual}.",
                ex.ExpectedVersion,
                ex.ActualVersion);

            return RateLimitConfigUpdateResult.Fail(
                "Rate limits were changed by someone else since this page was loaded. Reload to see the "
                + "current configuration, then reapply your change.",
                statusCode: 409);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist rate limit configuration.");
            return RateLimitConfigUpdateResult.Fail("Failed to persist rate limit configuration.", statusCode: 500);
        }
    }

    private static RateLimitPolicy ToPolicy(RateLimitTierOptions tier) =>
        new(tier.Rpm, tier.Burst, tier.MaxConcurrentStreams);

    private static RateLimitTierOptions ToTierOptions(RateLimitPolicy policy) =>
        new()
        {
            Rpm = policy.Rpm,
            Burst = policy.Burst,
            MaxConcurrentStreams = policy.MaxConcurrentStreams,
        };
}
