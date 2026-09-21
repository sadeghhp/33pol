namespace Pol33.Core.RateLimiting;

/// <summary>
/// Stable ids for the configured controls a request can be held to, as the per-limit usage report
/// spells them.
/// </summary>
/// <remarks>
/// A scoped rule's id is its identity, <c>scope:target</c>, lower-cased — the same string the admin
/// console derives from a rule, so the two join without either side guessing at names or order. The
/// tenant tiers are not rules and get ids of their own: <c>default</c> and <c>plan:&lt;slug&gt;</c>.
/// </remarks>
public static class RateLimitLimitIds
{
    public const string Default = "default";

    public const string PlanScope = "plan";

    public static string AuthFailure { get; } = Rule(RateLimitScopeNames.AuthFailure, RateLimitScopeNames.SingletonTarget);

    public static string Anonymous { get; } = Rule(RateLimitScopeNames.Anonymous, RateLimitScopeNames.SingletonTarget);

    public static string Global { get; } = Rule(RateLimitScopeNames.Global, RateLimitScopeNames.SingletonTarget);

    public static string Rule(string scope, string target) =>
        RateLimitScheduleProjection.Identity(scope, target).ToLowerInvariant();

    public static string Plan(string planSlug) => Rule(PlanScope, planSlug);

    /// <summary>Splits an id at its first colon; <c>default</c> has no target.</summary>
    public static (string Scope, string Target) Split(string limitId)
    {
        var colon = limitId.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (limitId, string.Empty) : (limitId[..colon], limitId[(colon + 1)..]);
    }

    /// <summary>
    /// Whether everything counted under this control drains one bucket. True for a rule that names
    /// one partition; false for a tier or protective scope, where every caller has a bucket of its
    /// own at the same size — so a rate summed across callers is not comparable with the limit.
    /// </summary>
    public static bool HasSingleBucket(string scope) =>
        scope is RateLimitScopeNames.Global
            or RateLimitScopeNames.Tenant
            or RateLimitScopeNames.ApiKey
            or RateLimitScopeNames.Model
            or RateLimitScopeNames.TenantModel
            or RateLimitScopeNames.ApiKeyModel;
}
