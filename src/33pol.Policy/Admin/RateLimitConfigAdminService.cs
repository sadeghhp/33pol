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
            Version = rateLimits.Version,
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

        AddSingleton(rules, rateLimits, RateLimitScopeNames.Global, rateLimits.Global);

        AddScope(rules, rateLimits, RateLimitScopeNames.Tenant, rateLimits.TenantOverrides);
        AddScope(rules, rateLimits, RateLimitScopeNames.ApiKey, rateLimits.ApiKeys);
        AddScope(rules, rateLimits, RateLimitScopeNames.Model, rateLimits.Models);
        AddScope(rules, rateLimits, RateLimitScopeNames.TenantModel, rateLimits.TenantModels);
        AddScope(rules, rateLimits, RateLimitScopeNames.ApiKeyModel, rateLimits.ApiKeyModels);

        AddSingleton(rules, rateLimits, RateLimitScopeNames.AuthFailure, rateLimits.AuthFailure);
        AddSingleton(rules, rateLimits, RateLimitScopeNames.Anonymous, rateLimits.Anonymous);

        return rules;
    }

    /// <summary>
    /// A scope with one partition. A switched-off rule lives in the side-car rather than the scope's
    /// tier, so it is looked for there first; the live tier is only a rule when it enforces something.
    /// </summary>
    private static void AddSingleton(
        List<RateLimitRuleDefinition> rules,
        Core.Configuration.RateLimitsConfigSection stored,
        string scope,
        RateLimitPolicy policy)
    {
        var target = RateLimitScopeNames.SingletonTarget;

        if (stored.DisabledRules.TryGetValue(RateLimitScheduleProjection.Identity(scope, target), out var off))
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(scope, target, off) with { Enabled = false });
            return;
        }

        if (!policy.EnforcesNothing)
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(scope, target, policy));
        }
    }

    /// <summary>
    /// Every rule in a scope, enforced or not, ordered together by target. Switching a rule off must
    /// not move it in the list — an operator reading the page should see it stay where it was and go
    /// grey, and a diff between two GETs should show one changed field rather than a reordering.
    /// </summary>
    private static void AddScope(
        List<RateLimitRuleDefinition> rules,
        Core.Configuration.RateLimitsConfigSection stored,
        string scope,
        IReadOnlyDictionary<string, RateLimitPolicy> map)
    {
        // Identities are "scope:target"; the colon is what keeps "tenant" from also matching
        // "tenant_model", and stripping a known prefix is what keeps a target that itself contains a
        // colon (an "llama3:8b" model id) intact.
        var prefix = scope + ":";
        var switchedOff = stored.DisabledRules
            .Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Target: p.Key[prefix.Length..], p.Value, Enabled: false));

        var all = map
            .Select(p => (Target: p.Key, p.Value, Enabled: true))
            .Concat(switchedOff)
            .OrderBy(static e => e.Target, StringComparer.Ordinal);

        foreach (var (target, policy, enabled) in all)
        {
            rules.Add(RateLimitRuleDefinition.FromPolicy(scope, target, policy) with { Enabled = enabled });
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
        return GetSchedule(WithSchedules(ToRules(stored), stored), at, from, to, take);
    }

    public RateLimitScheduleReport GetSchedule(
        IReadOnlyList<RateLimitRuleDefinition> rules,
        DateTimeOffset at,
        DateTimeOffset from,
        DateTimeOffset to,
        int take)
    {
        ArgumentNullException.ThrowIfNull(rules);

        // A switched-off rule enforces nothing, so it has nothing to say about what is in force or
        // when that changes; leaving it in would draw windows on the calendar that can never take
        // effect. Filtered here rather than in the builder, which stays a pure function of the rules
        // it is handed — both overloads funnel through this one, so neither can forget.
        var enforced = rules.Where(static r => r is { Enabled: true }).ToArray();
        return RateLimitScheduleReportBuilder.Build(enforced, at, from, to, take);
    }

    public RateLimitWindowPreview PreviewWindow(RateLimitRuleDefinition rule, string candidateName)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return RateLimitWindowPreviewBuilder.Build(rule, candidateName, _timeProvider.GetUtcNow());
    }

    /// <remarks>
    /// <para>Asks the same question <see cref="UpdateAsync"/> asks — is a settings repository
    /// there? — because that is the one precondition a save has beyond validation, and it is what
    /// differs between a deployment with a database and one without.</para>
    ///
    /// <para>Asked of the container rather than by resolving the service, which would construct a
    /// repository and with it a <c>DbContext</c> on every read of this page. Registration is the
    /// thing being tested; building an instance to find out both costs a context per request and
    /// makes a failure to construct one fail the <em>read</em>. This endpoint is how an operator
    /// looks at limits they cannot change, so it has to keep working in exactly the conditions that
    /// stop a write. A repository that is registered but cannot be built is still reported as
    /// writable here, and the save that follows reports the failure — which is where it belongs.</para>
    /// </remarks>
    public RateLimitWriteAvailability GetWriteAvailability()
    {
        using var scope = scopeFactory.CreateScope();
        var container = scope.ServiceProvider.GetService<IServiceProviderIsService>();

        // No such feature on this container (a hand-built one in a test): fall back to resolving,
        // which is what this used to do unconditionally.
        var registered = container is not null
            ? container.IsService(typeof(IRateLimitSettingsRepository))
            : scope.ServiceProvider.GetService<IRateLimitSettingsRepository>() is not null;

        return registered
            ? RateLimitWriteAvailability.Available
            : new RateLimitWriteAvailability(false, RateLimitWriteAvailability.StoreUnavailable);
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

            var newVersion = await repository
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
                enabled ? "Rate limits updated." : "Rate limits updated. Rate limiting is now disabled.",
                newVersion);
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
                "Rate limits were changed by someone else since this page was loaded. Review the current "
                + "configuration against your change before saving again.",
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
