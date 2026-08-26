using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Api.Contracts;

/// <summary>
/// GET response and PUT request body for <c>/admin/api/rate-limits</c>.
/// </summary>
public sealed class AdminRateLimitsDto
{
    /// <summary>
    /// Global master switch. When false the gateway enforces no request-rate or stream-concurrency
    /// limits. Tier values below are still persisted so the configuration survives a disable/enable cycle.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether load-aware adaptation may hold model limits below their configured rate while a model
    /// is saturated. Never raises a limit; see the adaptive section of the usage report for what it
    /// is currently doing and why.
    /// </summary>
    public bool AdaptiveEnabled { get; set; }

    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scoped rules: per-model, per-key, combined, and the global and auth-failure singletons.
    /// </summary>
    /// <remarks>
    /// Null on a PUT means "leave the stored rules alone", which is what a client written against
    /// the older contract sends. An empty array means "there are no rules" and does delete them, so
    /// the two are deliberately different: a client that cannot see rules must not be able to
    /// destroy them by omission.
    /// </remarks>
    public List<AdminRateLimitRuleDto>? Rules { get; set; }
}

/// <param name="Scope">
/// <c>global</c>, <c>tenant</c>, <c>api_key</c>, <c>model</c>, <c>tenant_model</c>,
/// <c>api_key_model</c> or <c>auth_failure</c>.
/// </param>
/// <param name="Target">
/// What the rule applies to: an id for the single-subject scopes, <c>subject|model</c> for the
/// combined ones, and <c>*</c> for the scopes with one partition.
/// </param>
/// <param name="Rpm">Sustained requests per minute; zero limits only concurrency.</param>
/// <param name="Burst">Extra requests an idle partition may spend at once.</param>
/// <param name="MaxConcurrentStreams">Concurrent streaming responses; zero means unlimited.</param>
public sealed record AdminRateLimitRuleDto(
    string Scope,
    string Target,
    int Rpm,
    int Burst,
    int MaxConcurrentStreams)
{
    public RateLimitRuleDefinition ToDefinition() =>
        new(Scope?.Trim() ?? string.Empty, Target ?? string.Empty, Rpm, Burst, MaxConcurrentStreams);

    public static AdminRateLimitRuleDto FromDefinition(RateLimitRuleDefinition rule) =>
        new(rule.Scope, rule.TargetKey, rule.Rpm, rule.Burst, rule.MaxConcurrentStreams);
}
