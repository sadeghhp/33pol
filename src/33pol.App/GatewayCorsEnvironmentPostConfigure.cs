using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App;

/// <summary>
/// Applies <c>GATEWAY_CORS_ALLOWED_ORIGIN_*</c> / <c>GATEWAY_CORS_ALLOWED_ORIGINS</c> to the bound
/// <see cref="GatewayOptions"/> when set, so Docker operators only need to edit <c>.env</c> (no
/// per-index Compose mappings).
/// </summary>
/// <remarks>
/// This copy feeds the database seed on first boot and the startup CORS diagnostics. The live CORS
/// policy reads the config snapshot, where <c>GatewayConfigState</c> overlays the same environment
/// origins on every snapshot — so the env vars take effect on every boot, not only the first.
/// </remarks>
internal sealed class GatewayCorsEnvironmentPostConfigure : IPostConfigureOptions<GatewayOptions>
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
