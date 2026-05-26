namespace Pol33.Core.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string ModelsConfigPath { get; set; } = "config/models.json";

    public int ConfigReloadIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// When null, enabled in Development and disabled otherwise.
    /// </summary>
    public bool? RegistryWatchEnabled { get; set; }

    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When false (default), backends are treated as healthy until the first probe completes.
    /// </summary>
    public bool HealthCheckStrictMode { get; set; }

    /// <summary>
    /// Inference API keys (Bearer or X-API-Key). Empty list disables inference auth (Development only recommended).
    /// </summary>
    public List<string> ApiKeys { get; set; } = [];

    /// <summary>
    /// Admin API keys for /admin/api/* routes. Empty disables admin auth when inference keys are also empty.
    /// </summary>
    public List<string> AdminApiKeys { get; set; } = [];

    /// <summary>
    /// When true, Production startup fails if no API keys are configured.
    /// </summary>
    public bool RequireApiKeysInProduction { get; set; } = true;

    public bool IsAuthenticationEnabled => ApiKeys.Count > 0 || AdminApiKeys.Count > 0;
}

