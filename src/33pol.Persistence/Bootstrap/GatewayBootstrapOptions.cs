namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayBootstrapOptions
{
    public const string SectionName = "Gateway:Bootstrap";

    public bool Enabled { get; init; } = true;

    public string TenantSlug { get; init; } = "default";

    public string TenantName { get; init; } = "Default Tenant";

    public string? AdminApiKey { get; init; }

    public string KeyPepper { get; init; } = "dev-pepper-change-me";
}
