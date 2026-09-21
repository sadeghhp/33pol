using System.Runtime.CompilerServices;

namespace Pol33.Core.RateLimiting;

/// <summary>Which of the two controls a decision came from.</summary>
public enum RateLimitControl
{
    /// <summary>The token bucket: how many requests per minute a partition may start.</summary>
    Rate = 0,

    /// <summary>The slot count: how many streaming responses a partition may hold open at once.</summary>
    Concurrency = 1,
}

/// <summary>
/// One rule in a request's rule set: a scope, the partition inside that scope this request belongs
/// to, and the budget that partition is held to.
/// </summary>
/// <param name="Scope">Which dimension this rule counts against.</param>
/// <param name="PartitionKey">
/// The bucket key. Already namespaced by scope, so two scopes can never collide on one bucket even
/// when they carry the same identifier — a tenant called <c>gpt-4</c> is not the model
/// <c>gpt-4</c>.
/// </param>
/// <param name="Policy">
/// The budget actually enforced, after any adaptive reduction. Never above what the operator
/// configured — see <paramref name="ConfiguredRpm"/>.
/// </param>
/// <param name="ConfiguredRpm">
/// The operator-configured sustained rate, before adaptation. Kept alongside the effective policy so
/// a report can show "600 configured, 420 in effect" rather than silently presenting the reduced
/// number as the limit.
/// </param>
/// <param name="AdaptiveFactor">
/// What the adaptive governor multiplied the configured rate by, in <c>(0, 1]</c>. Exactly
/// <c>1.0</c> when the rule is being enforced as configured, which is the case whenever adaptation
/// is disabled or the system is not under pressure.
/// </param>
public readonly record struct RateLimitRule(
    RateLimitScope Scope,
    string PartitionKey,
    RateLimitPolicy Policy,
    int ConfiguredRpm,
    double AdaptiveFactor)
{
    public RateLimitRule(RateLimitScope scope, string partitionKey, RateLimitPolicy policy)
        : this(scope, partitionKey, policy, policy.Rpm, 1.0)
    {
    }

    /// <summary>Whether the governor is currently holding this rule below its configured rate.</summary>
    public bool IsAdapted => AdaptiveFactor < 1.0;

    /// <summary>
    /// The configured control that supplies this rule's <em>rate</em>, as a stable lower-case id:
    /// a rule identity (<c>tenant:acme</c>, <c>model:gpt-4</c>, <c>global:*</c>), <c>plan:&lt;slug&gt;</c>,
    /// or <c>default</c>. Null when the rule was built outside the plan resolver, in which case the
    /// per-limit report does not hear about it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PartitionKey"/>, which names a bucket. One control can own many
    /// buckets — every tenant on a plan has its own — and the tenant scope's bucket can be governed
    /// by an override, a plan or the default tier, so the bucket key cannot say which number an
    /// operator has to edit. Resolved when the plan is built, which is cached, so carrying it costs
    /// the request path nothing.
    /// </remarks>
    public string? LimitId { get; init; }

    /// <summary>
    /// The control that supplies this rule's concurrent-stream cap. Differs from
    /// <see cref="LimitId"/> only inside the tenant scope, where an override with no rate of its own
    /// keeps the plan's rate and replaces only the cap.
    /// </summary>
    public string? StreamLimitId { get; init; }

    /// <summary>
    /// Whether this is the separate bucket a <c>model</c> rule keeps for anonymous callers. Same
    /// control, different bucket, so it is reported as its own row.
    /// </summary>
    public bool AnonymousBucket { get; init; }
}

/// <summary>
/// A stack buffer for the rules that apply to one request. Six is the ceiling by construction:
/// global, tenant, key, model, tenant×model, key×model — one per <see cref="RateLimitScope"/> that
/// a request can be subject to.
/// </summary>
/// <remarks>
/// An inline array rather than a rented or allocated one because this sits on the hot path of every
/// inference request. The rules are built into it, handed to the store as a
/// <see cref="System.Span{T}"/>, and go out of scope with the frame — no allocation, no pool to
/// return to, and no way to leak one across requests.
/// </remarks>
[InlineArray(MaxRules)]
public struct RateLimitRuleBuffer
{
    public const int MaxRules = 6;

#pragma warning disable IDE0051, CS0169 // The inline-array element; the runtime projects it into MaxRules slots.
    private RateLimitRule _element0;
#pragma warning restore IDE0051, CS0169
}
