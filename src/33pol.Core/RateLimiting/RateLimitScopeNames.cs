namespace Pol33.Core.RateLimiting;

/// <summary>
/// The scope labels as they are stored and transported: in the <c>rate_limit_rules</c> table, in the
/// admin API, and in metric tags.
/// </summary>
/// <remarks>
/// Constants rather than <c>RateLimitScope.ToString()</c> because these values are persisted. An
/// enum member renamed in a refactor would silently orphan every row written under the old name,
/// and the symptom would be limits quietly not applying.
/// </remarks>
public static class RateLimitScopeNames
{
    public const string Global = "global";
    public const string Tenant = "tenant";
    public const string ApiKey = "api_key";
    public const string Model = "model";
    public const string TenantModel = "tenant_model";
    public const string ApiKeyModel = "api_key_model";
    public const string AuthFailure = "auth_failure";

    /// <summary>
    /// The tier for callers with no credential — traffic to a <c>publicAccess</c> model, counted
    /// per client address block. A singleton like <see cref="AuthFailure"/>: one rule, target
    /// <see cref="SingletonTarget"/>. It selects the tier for the anonymous caller's tenant-scope
    /// bucket; there is no separate bucket dimension for it.
    /// </summary>
    public const string Anonymous = "anonymous";

    /// <summary>The target key used by scopes that have exactly one partition.</summary>
    public const string SingletonTarget = "*";

    /// <summary>Every scope an operator may configure a rule for, in evaluation order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Global, Tenant, ApiKey, Model, TenantModel, ApiKeyModel, AuthFailure, Anonymous];

    /// <summary>Whether the scope's target key is a <c>subject|model</c> pair.</summary>
    public static bool IsPair(string? scope) =>
        Is(scope, TenantModel) || Is(scope, ApiKeyModel);

    /// <summary>Whether the scope has exactly one partition, so its only valid target is <c>*</c>.</summary>
    public static bool IsSingleton(string? scope) =>
        Is(scope, Global) || Is(scope, AuthFailure) || Is(scope, Anonymous);

    /// <summary>
    /// Whether the scope is evaluated by a limiter that only ever meters the request rate, so a
    /// stream cap configured on it can never be read.
    /// </summary>
    /// <remarks>
    /// <c>auth_failure</c> is metered by <c>AuthFailureRateLimitMiddleware</c>, which peeks and debits
    /// a token bucket and nothing else. A rule there carrying only a stream cap passed the "enforces
    /// something" test while leaving the gateway on the default tier for credential guessing.
    /// </remarks>
    public static bool IsRateOnly(string? scope) => Is(scope, AuthFailure);

    public static bool IsKnown(string? scope) =>
        scope is not null && All.Contains(scope, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scope as it is stored and compared: the canonical constant when the spelling is one of
    /// <see cref="All"/> in any casing, otherwise the input trimmed and left alone.
    /// </summary>
    /// <remarks>
    /// <para>Recognition has always been case-insensitive (<see cref="IsKnown"/>) while the shape
    /// tests below were ordinal, so a rule submitted as <c>{"scope":"Anonymous","target":"acme"}</c>
    /// was recognised as a known scope, was <em>not</em> recognised as a singleton, skipped the
    /// "target must be <c>*</c>" check, and was stored — a rule the console then displayed and the
    /// engine could never match. The shape tests are case-insensitive now as well, and callers
    /// canonicalise on the way in so what is persisted is always the spelling everything else
    /// compares against.</para>
    ///
    /// <para>An unknown spelling is returned trimmed rather than rejected here: reporting it is
    /// <see cref="IsKnown"/>'s job, and it produces a better message with the original text in it.</para>
    /// </remarks>
    public static string Canonical(string? scope)
    {
        var trimmed = scope?.Trim() ?? string.Empty;
        foreach (var known in All)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return trimmed;
    }

    private static bool Is(string? scope, string name) =>
        string.Equals(scope, name, StringComparison.OrdinalIgnoreCase);
}
