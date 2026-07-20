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
/// Rate-limit section of the config snapshot. The resolver picks a tier in precedence order:
/// per-tenant override, then plan, then default. Plan/tenant keys are compared OrdinalIgnoreCase.
/// TenantOverrides is currently always empty (reserved), matching the pre-migration behavior where
/// the RateLimiting:Tenants map was never populated.
/// </summary>
public sealed record RateLimitsConfigSection
{
    /// <summary>
    /// Global master switch. When false, neither request-rate limits nor stream-concurrency limits are
    /// enforced for any tier. Lives on the section rather than on <see cref="RateLimitPolicy"/> because
    /// it is gateway-wide; the per-tier numbers stay meaningful and are restored when re-enabled.
    /// Defaults to true so a database-less deployment enforces limits exactly as before.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public RateLimitPolicy Default { get; init; } = RateLimitPolicy.Default;

    public IReadOnlyDictionary<string, RateLimitPolicy> Plans { get; init; } = EmptyMap;

    public IReadOnlyDictionary<string, RateLimitPolicy> TenantOverrides { get; init; } = EmptyMap;

    public static RateLimitsConfigSection Defaults { get; } = new();

    private static readonly IReadOnlyDictionary<string, RateLimitPolicy> EmptyMap =
        new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase);
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
