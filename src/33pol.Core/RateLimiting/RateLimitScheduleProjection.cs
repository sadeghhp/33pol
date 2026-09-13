using Pol33.Core.Configuration;

namespace Pol33.Core.RateLimiting;

/// <summary>
/// Turns the stored rate-limit section — base tiers plus schedules — into the effective section
/// the request path reads, at one instant, and says when that projection stops being right.
/// </summary>
/// <remarks>
/// The projected section is the same shape as the stored one, so every consumer of
/// <see cref="RateLimitsConfigSection"/> keeps reading maps of <see cref="RateLimitPolicy"/> and
/// never learns that a window exists. The stored section rides along in
/// <see cref="RateLimitsConfigSection.Stored"/> for the admin surface, which shows base tiers.
/// </remarks>
public static class RateLimitScheduleProjection
{
    /// <param name="stored">The section as loaded: base tiers and <see cref="RateLimitsConfigSection.Schedules"/>.</param>
    /// <param name="now">The instant to project at.</param>
    /// <param name="effectiveVersion">
    /// Stamped on the projection so caches keyed by configuration (the plan cache above all) miss
    /// when a window begins or ends, exactly as they do on an admin write.
    /// </param>
    /// <returns>The effective section and the earliest instant it may change; null when nothing is scheduled ahead.</returns>
    public static (RateLimitsConfigSection Effective, DateTimeOffset? NextTransition) Project(
        RateLimitsConfigSection stored,
        DateTimeOffset now,
        long effectiveVersion)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.Schedules.Count == 0)
        {
            return (stored, null);
        }

        DateTimeOffset? next = null;

        var effective = stored with
        {
            Stored = stored,
            EffectiveVersion = effectiveVersion,
            Global = Single(stored, RateLimitScopeNames.Global, stored.Global, now, ref next),
            AuthFailure = Single(stored, RateLimitScopeNames.AuthFailure, stored.AuthFailure, now, ref next),
            Anonymous = Single(stored, RateLimitScopeNames.Anonymous, stored.Anonymous, now, ref next),
            TenantOverrides = Map(stored, RateLimitScopeNames.Tenant, stored.TenantOverrides, now, ref next),
            ApiKeys = Map(stored, RateLimitScopeNames.ApiKey, stored.ApiKeys, now, ref next),
            Models = Map(stored, RateLimitScopeNames.Model, stored.Models, now, ref next),
            TenantModels = Map(stored, RateLimitScopeNames.TenantModel, stored.TenantModels, now, ref next),
            ApiKeyModels = Map(stored, RateLimitScopeNames.ApiKeyModel, stored.ApiKeyModels, now, ref next),
        };

        return (effective, next);
    }

    /// <summary>The identity a schedule is stored under: the rule's <c>scope:target</c>.</summary>
    public static string Identity(string scope, string target) => scope + ":" + target;

    private static RateLimitPolicy Single(
        RateLimitsConfigSection stored,
        string scope,
        RateLimitPolicy basePolicy,
        DateTimeOffset now,
        ref DateTimeOffset? next)
    {
        if (!stored.Schedules.TryGetValue(Identity(scope, RateLimitScopeNames.SingletonTarget), out var windows))
        {
            return basePolicy;
        }

        var evaluation = RateLimitScheduleEvaluator.Evaluate(basePolicy, windows, now);
        Consider(ref next, evaluation.NextTransition);
        return evaluation.Effective;
    }

    private static IReadOnlyDictionary<string, RateLimitPolicy> Map(
        RateLimitsConfigSection stored,
        string scope,
        IReadOnlyDictionary<string, RateLimitPolicy> basePolicies,
        DateTimeOffset now,
        ref DateTimeOffset? next)
    {
        Dictionary<string, RateLimitPolicy>? projected = null;

        foreach (var (target, basePolicy) in basePolicies)
        {
            if (!stored.Schedules.TryGetValue(Identity(scope, target), out var windows))
            {
                continue;
            }

            var evaluation = RateLimitScheduleEvaluator.Evaluate(basePolicy, windows, now);
            Consider(ref next, evaluation.NextTransition);

            projected ??= new Dictionary<string, RateLimitPolicy>(basePolicies, StringComparer.OrdinalIgnoreCase);
            projected[target] = evaluation.Effective;
        }

        return projected ?? basePolicies;
    }

    private static void Consider(ref DateTimeOffset? next, DateTimeOffset? candidate)
    {
        if (candidate is { } c && (next is null || c < next))
        {
            next = c;
        }
    }
}
