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

    /// <summary>The target key used by scopes that have exactly one partition.</summary>
    public const string SingletonTarget = "*";

    /// <summary>Every scope an operator may configure a rule for, in evaluation order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Global, Tenant, ApiKey, Model, TenantModel, ApiKeyModel, AuthFailure];

    /// <summary>Whether the scope's target key is a <c>subject|model</c> pair.</summary>
    public static bool IsPair(string scope) =>
        scope is TenantModel or ApiKeyModel;

    /// <summary>Whether the scope has exactly one partition, so its only valid target is <c>*</c>.</summary>
    public static bool IsSingleton(string scope) =>
        scope is Global or AuthFailure;

    public static bool IsKnown(string? scope) =>
        scope is not null && All.Contains(scope, StringComparer.OrdinalIgnoreCase);
}
