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
    ILogger<RateLimitConfigAdminService> logger) : IRateLimitConfigAdminService
{
    public RateLimitAdminConfig GetCurrent()
    {
        var rateLimits = configProvider.Current.RateLimits;
        return new RateLimitAdminConfig
        {
            Enabled = rateLimits.Enabled,
            AdaptiveEnabled = rateLimits.AdaptiveEnabled,
            Default = ToTierOptions(rateLimits.Default),
            Plans = rateLimits.Plans.ToDictionary(
                static p => p.Key,
                static p => ToTierOptions(p.Value),
                StringComparer.OrdinalIgnoreCase),
            Rules = ToRules(rateLimits),
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

    public async Task<RateLimitConfigUpdateResult> UpdateAsync(
        bool enabled,
        bool adaptiveEnabled,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        IReadOnlyList<RateLimitRuleDefinition>? rules = null,
        CancellationToken cancellationToken = default)
    {
        // Tier values are validated even when disabling, so re-enabling later cannot restore a
        // configuration that was never checked.
        if (!RateLimitConfigValidation.TryValidate(defaultTier, plans, out var validationError))
        {
            return RateLimitConfigUpdateResult.Fail(validationError!, statusCode: 400);
        }

        if (!RateLimitConfigValidation.TryValidateRules(rules, out var ruleError))
        {
            return RateLimitConfigUpdateResult.Fail(ruleError!, statusCode: 400);
        }

        // Null means "the caller does not manage rules", so the stored set is carried through
        // unchanged rather than wiped by a client that predates them.
        var effectiveRules = rules ?? ToRules(configProvider.Current.RateLimits);

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
                    cancellationToken)
                .ConfigureAwait(false);

            var refresher = scope.ServiceProvider.GetService<IGatewayConfigRefresher>();
            if (refresher is not null)
            {
                await refresher.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Updated rate limits (enabled={Enabled}, adaptive={Adaptive}, default + {PlanCount} plan tier(s), {RuleCount} scoped rule(s)).",
                enabled,
                adaptiveEnabled,
                plans.Count,
                effectiveRules.Count);
            return RateLimitConfigUpdateResult.Ok(
                enabled ? "Rate limits updated." : "Rate limits updated. Rate limiting is now disabled.");
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
