namespace Pol33.Security.Configuration;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "Gateway:Security";

    /// <summary>Well-known development default; rejected at startup outside Development.</summary>
    public const string DefaultKeyPepper = "dev-pepper-change-me";

    /// <summary>Minimum accepted pepper length outside Development.</summary>
    public const int MinimumPepperLength = 16;

    public string KeyPepper { get; set; } = DefaultKeyPepper;

    /// <summary>
    /// Maximum age of a cached API-key validation or model-grant decision.
    /// </summary>
    /// <remarks>
    /// This is the gateway's revocation SLA. Invalidation on write is in-process only, so on a
    /// multi-replica deployment a revoked key or a removed model grant keeps being accepted by the
    /// other replicas until their cached entry expires. That window is exactly this value, which is
    /// why it is capped by <see cref="MaximumCacheTtlMinutes"/> rather than left unbounded.
    /// </remarks>
    public int CacheTtlMinutes { get; set; } = 2;

    /// <summary>
    /// Upper bound on <see cref="CacheTtlMinutes"/>. A longer TTL would mean revocation of a
    /// compromised credential takes more than five minutes to take effect across replicas, which is
    /// not an acceptable security posture for a credential-bearing gateway.
    /// </summary>
    public const int MaximumCacheTtlMinutes = 5;

    /// <summary>
    /// Explicit opt-in to run without API-key authentication when no database is configured.
    /// Only honored outside Development; the default (false) makes such a deployment fail startup
    /// rather than silently expose every endpoint anonymously.
    /// </summary>
    public bool AllowAnonymous { get; set; }
}
