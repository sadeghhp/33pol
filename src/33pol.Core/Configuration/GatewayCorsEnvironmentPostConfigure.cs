using Microsoft.Extensions.Options;

namespace Pol33.Core.Configuration;

/// <summary>
/// Applies <c>GATEWAY_CORS_ALLOWED_ORIGIN_*</c> / <c>GATEWAY_CORS_ALLOWED_ORIGINS</c> when set,
/// so Docker operators only need to edit <c>.env</c> (no per-index Compose mappings).
/// </summary>
public sealed class GatewayCorsEnvironmentPostConfigure : IPostConfigureOptions<GatewayOptions>
{
    public void PostConfigure(string? name, GatewayOptions options)
    {
        var envOrigins = GatewayCorsEnvironmentConfiguration.ReadAllowedOriginsFromEnvironment();
        if (envOrigins.Length > 0)
        {
            options.Cors.AllowedOrigins = envOrigins;
        }
    }
}
