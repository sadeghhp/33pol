namespace Pol33.Core.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string ModelsConfigPath { get; set; } = "config/models.json";

    /// <summary>
    /// Encrypted upstream API keys for models using <c>upstreamAuth.secretRef</c>.
    /// </summary>
    public string UpstreamSecretsPath { get; set; } = "config/upstream-secrets.enc";

    /// <summary>
    /// Extra environment variable names the gateway may read an upstream bearer token from, beyond
    /// the built-in providers' variables and the <c>*_API_KEY</c> / <c>*_TOKEN</c> convention.
    /// Anything not permitted by <see cref="Providers.UpstreamEnvVarPolicy"/> is refused, so an admin
    /// cannot have the gateway read an unrelated host secret and forward it to an upstream.
    /// </summary>
    public string[] UpstreamEnvVarAllowList { get; set; } = [];

    public int ConfigReloadIntervalSeconds { get; set; } = 2;

    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When true, <c>models.json</c> changes are detected via <see cref="System.IO.FileSystemWatcher"/> (debounced).
    /// When false, changes are detected via SHA-256 polling at <see cref="ConfigReloadIntervalSeconds"/>.
    /// </summary>
    public bool RegistryWatchEnabled { get; set; }

    /// <summary>
    /// When false (default), backends are treated as healthy until the first probe completes.
    /// </summary>
    public bool HealthCheckStrictMode { get; set; }

    public GatewayResilienceOptions Resilience { get; set; } = new();

    public GatewayTlsOptions Tls { get; set; } = new();

    public GatewayCorsOptions Cors { get; set; } = new();
}
