namespace Pol33.Core.RateLimiting;

/// <summary>
/// A scoped rule as an operator configures it: which scope, what inside that scope, and the tier.
/// </summary>
/// <param name="Scope">One of <see cref="RateLimitScopeNames"/>.</param>
/// <param name="TargetKey">
/// The model id, tenant id or key id the rule applies to; <c>subject|model</c> for the combined
/// scopes; <see cref="RateLimitScopeNames.SingletonTarget"/> for the scopes with one partition.
/// </param>
/// <param name="Rpm">Sustained requests per minute; zero means this rule does not limit the rate.</param>
/// <param name="Burst">Extra tokens above <paramref name="Rpm"/> an idle partition may spend at once.</param>
/// <param name="MaxConcurrentStreams">Concurrent streaming responses; zero means unlimited.</param>
public sealed record RateLimitRuleDefinition(
    string Scope,
    string TargetKey,
    int Rpm,
    int Burst,
    int MaxConcurrentStreams)
{
    public RateLimitPolicy ToPolicy() => new(Rpm, Burst, MaxConcurrentStreams);

    public static RateLimitRuleDefinition FromPolicy(string scope, string targetKey, RateLimitPolicy policy) =>
        new(scope, targetKey, policy.Rpm, policy.Burst, policy.MaxConcurrentStreams);

    /// <summary>Identity for de-duplication: two rules with the same scope and target are the same rule.</summary>
    public string Identity => Scope + ":" + TargetKey;
}
