namespace Pol33.Core.RateLimiting;

/// <summary>
/// The dimension a rate-limit rule counts against. Every scope that applies to a request is
/// evaluated; the scopes do not override one another, so a request is admitted only when all of
/// them admit it.
/// </summary>
/// <remarks>
/// <para>The declaration order is the evaluation order, and it is deliberate in two ways.</para>
///
/// <para>First, it is the fixed order every caller acquires in. Multi-scope admission takes a token
/// from each applicable bucket and refunds them all if a later scope refuses, so a stable order is
/// what makes the refund set well defined regardless of which scopes a given request happens to
/// have.</para>
///
/// <para>Second, the model-independent scopes come first because the model is not free to learn.
/// Reading it means buffering and parsing the request body, and a caller that is already over its
/// tenant or key budget must be refused before the gateway pays for that. Everything below
/// <see cref="Model"/> is therefore evaluated in a second stage, after the parse, and only for
/// requests the first stage admitted.</para>
/// </remarks>
public enum RateLimitScope
{
    /// <summary>Every inference request through the gateway, regardless of caller or model.</summary>
    Global = 0,

    /// <summary>
    /// One tenant, or — for unauthenticated traffic to a public model — one client address block.
    /// This is the scope the gateway enforced before scoped rules existed.
    /// </summary>
    Tenant = 1,

    /// <summary>One API key. Bounds a single credential inside its tenant's larger allowance.</summary>
    ApiKey = 2,

    /// <summary>One model, summed across every caller. The model's own capacity, shared fairly.</summary>
    Model = 3,

    /// <summary>One tenant's use of one model.</summary>
    TenantModel = 4,

    /// <summary>One API key's use of one model — the narrowest scope there is.</summary>
    ApiKeyModel = 5,

    /// <summary>
    /// Requests refused by authentication, counted per client address. Evaluated by its own
    /// middleware ahead of the security layer, never as part of a request's rule set.
    /// </summary>
    AuthFailure = 6,
}

public static class RateLimitScopeExtensions
{
    /// <summary>Stable lower-case label for headers, metrics tags and report rows.</summary>
    public static string ToLabel(this RateLimitScope scope) => scope switch
    {
        RateLimitScope.Global => "global",
        RateLimitScope.Tenant => "tenant",
        RateLimitScope.ApiKey => "api_key",
        RateLimitScope.Model => "model",
        RateLimitScope.TenantModel => "tenant_model",
        RateLimitScope.ApiKeyModel => "api_key_model",
        RateLimitScope.AuthFailure => "auth_failure",
        _ => "unknown",
    };

    /// <summary>
    /// Whether the scope needs the request's model, and therefore belongs to the second evaluation
    /// stage that runs after the body has been parsed.
    /// </summary>
    public static bool RequiresModel(this RateLimitScope scope) =>
        scope is RateLimitScope.Model or RateLimitScope.TenantModel or RateLimitScope.ApiKeyModel;

    /// <summary>
    /// Whether a refusal from this scope is the caller's own doing, rather than a budget it shares
    /// with everybody else.
    /// </summary>
    /// <remarks>
    /// The adaptive governor escalates a partition's <c>Retry-After</c> the longer it keeps being
    /// refused, on the premise that a client refused over and over is retrying too fast. That
    /// premise only holds for a limit the caller alone can exhaust. <see cref="Global"/> and
    /// <see cref="Model"/> are shared: a tenant well inside its own tier, sending one request a
    /// minute, was accumulating escalation because the <em>gateway</em> was busy — and then being
    /// told to wait a minute for a limit it was never over. Refusals from a shared scope are still
    /// refusals; they simply say nothing about this caller's pacing.
    /// </remarks>
    public static bool IsCallerScoped(this RateLimitScope scope) =>
        scope is RateLimitScope.Tenant
            or RateLimitScope.ApiKey
            or RateLimitScope.TenantModel
            or RateLimitScope.ApiKeyModel;
}
