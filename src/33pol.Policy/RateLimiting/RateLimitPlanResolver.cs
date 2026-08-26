using System.Collections.Concurrent;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Policy.RateLimiting;

/// <summary>
/// Builds the rule set for a request — one rule per scope that applies to it — and caches the result
/// so the hot path is a dictionary lookup rather than a rebuild.
/// </summary>
/// <remarks>
/// <para>Resolving from scratch means six map lookups, two string concatenations for the combined
/// scopes, and an array. Doing that on every inference request would put more work in front of the
/// limiter than the limiter itself costs, so a resolved plan is cached per distinct
/// (tenant, key, plan, model) combination and reused until the configuration changes.</para>
///
/// <para>The cache key carries the config snapshot's version, so an admin edit invalidates every
/// entry the moment it lands — there is no TTL to wait out and no staleness window in which the
/// gateway enforces a limit an operator has already changed. It also carries the adaptive factor,
/// <em>quantised</em> to a small number of steps: baking the raw factor in would mint a fresh entry
/// every time the governor nudged a model by a thousandth, while quantising bounds the churn to a
/// fixed number of variants per model and costs at most one step of precision on a number that is
/// already an approximation.</para>
/// </remarks>
public sealed class RateLimitPlanResolver(
    IGatewayConfigProvider configProvider,
    IAdaptiveRateLimitGovernor? governor = null) : IRateLimitPlanResolver
{
    /// <summary>
    /// Adaptive factors are rounded to this many steps for cache-key purposes: 20 steps is a 5%
    /// granularity, finer than the governor's own recovery increment.
    /// </summary>
    private const int FactorQuantisationSteps = 20;

    /// <summary>
    /// Cache ceiling. Entries are keyed by identity and model, so the natural size is
    /// (keys × models); the ceiling only matters when anonymous traffic pushes the identity count up.
    /// Past it the cache is dropped wholesale rather than evicted one at a time — rebuilding a plan
    /// is cheap, and an LRU here would cost more to maintain than it saves.
    /// </summary>
    private const int MaxCacheEntries = 20_000;

    private readonly ConcurrentDictionary<PlanCacheKey, RateLimitPlan> _cache = new();

    public bool IsEnabled() => configProvider.Current.RateLimits.Enabled;

    public bool HasModelScopedRules()
    {
        var rateLimits = configProvider.Current.RateLimits;
        return rateLimits.Models.Count > 0 ||
               rateLimits.TenantModels.Count > 0 ||
               rateLimits.ApiKeyModels.Count > 0;
    }

    public RateLimitPlan Resolve(in RateLimitSubject subject, string? modelId)
    {
        var snapshot = configProvider.Current;
        var rateLimits = snapshot.RateLimits;

        var factorStep = ModelFactorStep(rateLimits, modelId);
        var key = new PlanCacheKey(
            snapshot.Version,
            subject.PartitionKey,
            subject.TenantId,
            subject.PlanSlug,
            subject.ApiKeyId,
            modelId,
            factorStep);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var plan = Build(rateLimits, subject, modelId, factorStep);

        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[key] = plan;
        return plan;
    }

    private int ModelFactorStep(RateLimitsConfigSection rateLimits, string? modelId)
    {
        if (modelId is null || governor is null || !rateLimits.AdaptiveEnabled || !governor.IsEnabled)
        {
            return FactorQuantisationSteps;
        }

        var factor = Math.Clamp(governor.GetModelFactor(modelId), 0.0, 1.0);
        return (int)Math.Round(factor * FactorQuantisationSteps);
    }

    private static RateLimitPlan Build(
        RateLimitsConfigSection rateLimits,
        in RateLimitSubject subject,
        string? modelId,
        int factorStep)
    {
        var rules = new List<RateLimitRule>(RateLimitRuleBuffer.MaxRules);

        // --- Stage one: everything that can be decided before the body is parsed. ---

        if (!rateLimits.Global.EnforcesNothing)
        {
            rules.Add(new RateLimitRule(RateLimitScope.Global, RateLimitKeys.GlobalPartition, rateLimits.Global));
        }

        // The tenant scope always produces a rule: it is the gateway's universal limit, and the
        // default tier stands in when nothing more specific is configured.
        var tenantTier = RateLimitPolicyResolver.ResolveTenantTier(rateLimits, subject.PlanSlug, subject.TenantId);
        rules.Add(new RateLimitRule(
            RateLimitScope.Tenant,
            RateLimitKeys.Tenant(subject.PartitionKey),
            tenantTier));

        if (!string.IsNullOrEmpty(subject.ApiKeyId) &&
            rateLimits.ApiKeys.TryGetValue(subject.ApiKeyId, out var keyTier) &&
            !keyTier.EnforcesNothing)
        {
            rules.Add(new RateLimitRule(RateLimitScope.ApiKey, RateLimitKeys.ApiKey(subject.ApiKeyId), keyTier));
        }

        var modelStageIndex = rules.Count;

        // --- Stage two: everything that needs the model. ---

        if (!string.IsNullOrEmpty(modelId))
        {
            var factor = (double)factorStep / FactorQuantisationSteps;

            if (rateLimits.Models.TryGetValue(modelId, out var modelTier) && !modelTier.EnforcesNothing)
            {
                rules.Add(Adapt(RateLimitScope.Model, RateLimitKeys.Model(modelId), modelTier, factor));
            }

            if (!string.IsNullOrEmpty(subject.TenantId) &&
                rateLimits.TenantModels.TryGetValue(RateLimitKeys.Pair(subject.TenantId, modelId), out var tenantModelTier) &&
                !tenantModelTier.EnforcesNothing)
            {
                rules.Add(Adapt(
                    RateLimitScope.TenantModel,
                    RateLimitKeys.TenantModel(subject.PartitionKey, modelId),
                    tenantModelTier,
                    factor));
            }

            if (!string.IsNullOrEmpty(subject.ApiKeyId) &&
                rateLimits.ApiKeyModels.TryGetValue(RateLimitKeys.Pair(subject.ApiKeyId, modelId), out var keyModelTier) &&
                !keyModelTier.EnforcesNothing)
            {
                rules.Add(Adapt(
                    RateLimitScope.ApiKeyModel,
                    RateLimitKeys.ApiKeyModel(subject.ApiKeyId, modelId),
                    keyModelTier,
                    factor));
            }
        }

        return new RateLimitPlan([.. rules], modelStageIndex);
    }

    /// <summary>
    /// A model-scoped rule with the governor's reduction applied, keeping the configured rate
    /// alongside it so a report can show both.
    /// </summary>
    private static RateLimitRule Adapt(
        RateLimitScope scope,
        string partitionKey,
        RateLimitPolicy tier,
        double factor)
    {
        if (factor >= 1.0)
        {
            return new RateLimitRule(scope, partitionKey, tier);
        }

        return new RateLimitRule(scope, partitionKey, tier.Scale(factor), tier.Rpm, factor);
    }

    /// <param name="ConfigVersion">
    /// Bumped by every admin write, so a change to any tier invalidates every cached plan at once
    /// rather than leaving some requests enforced against the old numbers.
    /// </param>
    /// <param name="FactorStep">The quantised adaptive factor for <paramref name="ModelId"/>.</param>
    private readonly record struct PlanCacheKey(
        long ConfigVersion,
        string PartitionKey,
        string? TenantId,
        string? PlanSlug,
        string? ApiKeyId,
        string? ModelId,
        int FactorStep);
}
