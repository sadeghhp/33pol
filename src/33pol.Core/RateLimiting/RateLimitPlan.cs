namespace Pol33.Core.RateLimiting;

/// <summary>
/// The full set of rules that applies to one request, split into the two stages the gateway
/// evaluates them in.
/// </summary>
/// <remarks>
/// Immutable and shared: the resolver caches one instance per distinct
/// (tenant, key, model, config version) combination, so the hot path is a dictionary lookup rather
/// than a rule-set rebuild. Nothing here holds mutable state — the counters live in the store, keyed
/// by the partition keys these rules name.
/// </remarks>
public sealed class RateLimitPlan
{
    public static RateLimitPlan Empty { get; } = new([], 0);

    public RateLimitPlan(RateLimitRule[] rules, int modelStageIndex)
    {
        Rules = rules;
        ModelStageIndex = modelStageIndex;
    }

    /// <summary>Every rule, model-independent ones first.</summary>
    public RateLimitRule[] Rules { get; }

    /// <summary>Index of the first model-scoped rule; equal to <c>Rules.Length</c> when there are none.</summary>
    public int ModelStageIndex { get; }

    /// <summary>
    /// The rules that can be evaluated before the request body is parsed: global, tenant, and key.
    /// These gate the parse, so a caller already over budget is refused without the gateway paying
    /// to read what it sent.
    /// </summary>
    public ReadOnlySpan<RateLimitRule> IdentityRules => Rules.AsSpan(0, ModelStageIndex);

    /// <summary>The rules that need the model: per-model, tenant×model, and key×model.</summary>
    public ReadOnlySpan<RateLimitRule> ModelRules => Rules.AsSpan(ModelStageIndex);

    public bool IsEmpty => Rules.Length == 0;

    /// <summary>Whether any rule in the set caps concurrency, so the router can skip the call entirely.</summary>
    public bool HasConcurrencyRules
    {
        get
        {
            foreach (var rule in Rules)
            {
                if (rule.Policy.EnforcesConcurrency)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// Who and what a request is, in the terms the rule set is built from. A struct passed by
/// <c>in</c> so resolving costs no allocation.
/// </summary>
/// <param name="TenantId">The authenticated tenant id, or null for anonymous traffic.</param>
/// <param name="PlanSlug">The tenant's plan, which selects a tier when no per-tenant rule exists.</param>
/// <param name="ApiKeyId">The credential the request arrived with, or null when anonymous.</param>
/// <param name="PartitionKey">
/// What the tenant scope counts against: the tenant id when authenticated, <c>anon:&lt;block&gt;</c>
/// otherwise. Resolved by the proxy layer, which is the only place that can see the connection.
/// </param>
public readonly record struct RateLimitSubject(
    string? TenantId,
    string? PlanSlug,
    string? ApiKeyId,
    string PartitionKey);
