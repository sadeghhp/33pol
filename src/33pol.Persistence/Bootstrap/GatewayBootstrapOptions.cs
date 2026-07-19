namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayBootstrapOptions
{
    public const string SectionName = "Gateway:Bootstrap";

    public bool Enabled { get; init; } = true;

    public string TenantSlug { get; init; } = "default";

    public string TenantName { get; init; } = "Default Tenant";

    public string? AdminApiKey { get; init; }

    public string KeyPepper { get; init; } = "dev-pepper-change-me";

    /// <summary>Minimum accepted pepper length outside Development. Mirrors Gateway:Security:KeyPepper.</summary>
    public const int MinimumPepperLength = 16;

    /// <summary>Minimum accepted admin API key length outside Development (when a value is supplied).</summary>
    public const int MinimumAdminKeyLength = 24;
}
