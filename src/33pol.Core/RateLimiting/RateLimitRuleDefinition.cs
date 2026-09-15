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
    /// <summary>
    /// The rule's schedule windows. Null means "not specified": the admin API reads it as "keep the
    /// stored schedule for this rule", so a client that predates windows cannot delete them by
    /// omission. An empty list is a deliberate "no windows". Never null once loaded from storage.
    /// </summary>
    public IReadOnlyList<RateLimitWindowDefinition>? Schedule { get; init; }

    /// <summary>
    /// Whether the rule is enforced. A disabled rule keeps its tier and its windows and is still
    /// listed by the admin API; it is simply absent from the maps the request path reads, so it
    /// enforces nothing — the difference between switching a limit off and deleting it.
    /// </summary>
    /// <remarks>
    /// Defaults to true, so a client that predates the flag and a rule loaded from a row written
    /// before the column existed both read as enforced.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>The windows to evaluate: the schedule, or none.</summary>
    public IReadOnlyList<RateLimitWindowDefinition> Windows => Schedule ?? [];

    public bool HasWindows => Schedule is { Count: > 0 };

    /// <summary>The base tier: what applies whenever no window is active.</summary>
    public RateLimitPolicy ToPolicy() => new(Rpm, Burst, MaxConcurrentStreams);

    public static RateLimitRuleDefinition FromPolicy(string scope, string targetKey, RateLimitPolicy policy) =>
        new(scope, targetKey, policy.Rpm, policy.Burst, policy.MaxConcurrentStreams);

    /// <summary>Identity for de-duplication: two rules with the same scope and target are the same rule.</summary>
    public string Identity => Scope + ":" + TargetKey;
}
