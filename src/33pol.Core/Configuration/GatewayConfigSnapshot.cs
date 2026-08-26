using Pol33.Core.RateLimiting;

namespace Pol33.Core.Configuration;

/// <summary>
/// Immutable aggregate of all database-backed operational configuration, delivered to the request
/// hot path via <see cref="Pol33.Core.Abstractions.IGatewayConfigProvider"/>. It grows one section
/// per migrated config area (CORS, rate limits, model routes, quota); this initial version carries
/// only the config <see cref="Version"/>.
///
/// <para><see cref="Version"/> is the monotonic config version stored in the database. The snapshot
/// syncer polls it to detect out-of-band changes and an admin write bumps it so a direct in-process
/// refresh can be confirmed. Init-only members keep adding sections a non-breaking change.</para>
/// </summary>
public sealed record GatewayConfigSnapshot
{
    public long Version { get; init; }

    public CorsConfigSection Cors { get; init; } = CorsConfigSection.Empty;

    public RateLimitsConfigSection RateLimits { get; init; } = RateLimitsConfigSection.Defaults;

    public QuotaConfigSection Quota { get; init; } = QuotaConfigSection.Defaults;

    /// <summary>The safe, hardcoded configuration used before the first successful database load.</summary>
    public static GatewayConfigSnapshot Defaults { get; } = new();
}

/// <summary>CORS section of the config snapshot: the normalized allowed origins (may be empty = deny).</summary>
public sealed record CorsConfigSection
{
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public static CorsConfigSection Empty { get; } = new();
}

/// <summary>
/// Rate-limit section of the config snapshot.
/// </summary>
/// <remarks>
/// <para>The scopes here compose rather than override one another: a request is admitted only when
/// every scope that applies to it admits it. Adding a narrower rule can therefore only tighten what
/// a caller may do, which is what makes the outcome independent of the order rules were configured
/// in.</para>
///
/// <para>Precedence applies <em>within</em> the tenant scope only, where three sources can each name
/// a tier for the same caller: a per-tenant override wins, then the tenant's plan, then the default.
/// Every other scope has exactly one source, so there is nothing to resolve. All keys are compared
/// OrdinalIgnoreCase.</para>
///
/// <para><see cref="TenantOverrides"/>, <see cref="Models"/> and the combined maps are populated
/// from the database on a database-backed deployment and from the <c>RateLimiting</c> section of
/// appsettings on a database-less one; both sources produce the same shape, and the database wins
/// where a deployment has one.</para>
/// </remarks>
public sealed record RateLimitsConfigSection
{
    /// <summary>
    /// Declared before <see cref="Defaults"/> on purpose. Static field initialisers run in
    /// declaration order, so a <c>Defaults</c> built above this line captures a null for every map
    /// that defaults to it — and the symptom is a NullReferenceException from whichever consumer
    /// first enumerates one, far from the cause.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RateLimitPolicy> EmptyMap =
        new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Global master switch. When false, neither request-rate limits nor stream-concurrency limits are
    /// enforced for any tier. Lives on the section rather than on <see cref="RateLimitPolicy"/> because
    /// it is gateway-wide; the per-tier numbers stay meaningful and are restored when re-enabled.
    /// Defaults to true so a database-less deployment enforces limits exactly as before.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether load-aware adaptation may reduce the configured tiers. Separate from
    /// <see cref="Enabled"/> so an operator can switch off the clever half without switching off
    /// enforcement — the order you want in an incident.
    /// </summary>
    public bool AdaptiveEnabled { get; init; }

    public RateLimitPolicy Default { get; init; } = RateLimitPolicy.Default;

    public IReadOnlyDictionary<string, RateLimitPolicy> Plans { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, RateLimitPolicy> TenantOverrides { get; init; } = EmptyMap;

    /// <summary>A ceiling on all inference traffic; <see cref="RateLimitPolicy.Unlimited"/> when unset.</summary>
    public RateLimitPolicy Global { get; init; } = RateLimitPolicy.Unlimited;

    /// <summary>Per-model limits, keyed by canonical model id.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> Models { get; init; } = EmptyMap;

    /// <summary>Per-API-key limits, keyed by key id.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> ApiKeys { get; init; } = EmptyMap;

    /// <summary>Combined limits keyed <c>tenantId|modelId</c>.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> TenantModels { get; init; } = EmptyMap;

    /// <summary>Combined limits keyed <c>apiKeyId|modelId</c>.</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> ApiKeyModels { get; init; } = EmptyMap;

    /// <summary>
    /// The budget for requests authentication refuses, per client address block.
    /// <see cref="RateLimitPolicy.Unlimited"/> means "use the default tier", which is what
    /// deployments that never configured one get.
    /// </summary>
    public RateLimitPolicy AuthFailure { get; init; } = RateLimitPolicy.Unlimited;

    public static RateLimitsConfigSection Defaults { get; } = new();
}

/// <summary>
/// Quota section of the config snapshot: the runtime-tunable per-partition monthly token limit and
/// the soft-warning ratio, read per request by the quota service. The defaults mirror
/// <see cref="QuotaOptions"/> so a database-less deployment (and the pre-load window) behaves exactly
/// as it did when these values were read straight from appsettings. Non-tunable scalars
/// (<c>CommittedRequestIdRetentionLimit</c>, resilience knobs, the key pepper) stay in appsettings.
/// </summary>
public sealed record QuotaConfigSection
{
    public long DefaultMonthlyTokenLimit { get; init; } = 1_000_000;

    public double SoftLimitRatio { get; init; } = 0.9;

    public static QuotaConfigSection Defaults { get; } = new();
}
