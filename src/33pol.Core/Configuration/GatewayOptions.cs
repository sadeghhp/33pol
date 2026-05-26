namespace Pol33.Core.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string ModelsConfigPath { get; set; } = "config/models.json";

    public int ConfigReloadIntervalSeconds { get; set; } = 30;

    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// When false (default), backends are treated as healthy until the first probe completes.
    /// </summary>
    public bool HealthCheckStrictMode { get; set; }
}
