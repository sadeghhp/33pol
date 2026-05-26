namespace Pol33.Security.Configuration;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "Gateway:Security";

    public string KeyPepper { get; set; } = "dev-pepper-change-me";

    public int CacheTtlMinutes { get; set; } = 2;
}
