namespace Pol33.Security.Configuration;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "Gateway:Security";

    public string KeyPepper { get; set; } = "dev-pepper-change-me";

    public int CacheTtlMinutes { get; set; } = 2;

    /// <summary>
    /// Explicit opt-in to run without API-key authentication when no database is configured.
    /// Only honored outside Development; the default (false) makes such a deployment fail startup
    /// rather than silently expose every endpoint anonymously.
    /// </summary>
    public bool AllowAnonymous { get; set; }
}
