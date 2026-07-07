namespace Pol33.Security.Configuration;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "Gateway:Security";

    /// <summary>Well-known development default; rejected at startup outside Development.</summary>
    public const string DefaultKeyPepper = "dev-pepper-change-me";

    /// <summary>Minimum accepted pepper length outside Development.</summary>
    public const int MinimumPepperLength = 16;

    public string KeyPepper { get; set; } = DefaultKeyPepper;

    public int CacheTtlMinutes { get; set; } = 2;

    /// <summary>
    /// Explicit opt-in to run without API-key authentication when no database is configured.
    /// Only honored outside Development; the default (false) makes such a deployment fail startup
    /// rather than silently expose every endpoint anonymously.
    /// </summary>
    public bool AllowAnonymous { get; set; }
}
