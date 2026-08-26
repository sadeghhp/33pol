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
    /// Consecutive failed sweeps before a backend is taken out of service.
    /// </summary>
    /// <remarks>
    /// <para>An unhealthy backend is refused at admission, so a single failed probe used to cause a
    /// full outage for that model until the next successful sweep — up to
    /// <see cref="HealthCheckIntervalSeconds"/> later. The probe endpoint is served by the same
    /// process that is generating, so a saturated model server answers it slowly precisely when it is
    /// busiest: the probe failed <em>because</em> the model was under load, and the gateway responded
    /// by refusing all of its traffic.</para>
    ///
    /// <para>Requiring consecutive failures costs detection latency for a genuinely dead backend
    /// (threshold × interval) and buys immunity to a single slow sweep. Recovery is not delayed: one
    /// successful probe restores service immediately. Set to 1 to restore the previous behaviour.</para>
    /// </remarks>
    public int HealthCheckUnhealthyThreshold { get; set; } = 2;

    /// <summary>
    /// Per-probe HTTP timeout for the background health sweep.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed what a <em>busy</em> backend takes to answer, not an idle one — see
    /// <see cref="HealthCheckUnhealthyThreshold"/>. Note the sweep tries each of the probe paths in
    /// turn, so a fully unreachable backend can take up to this many seconds per path.
    /// </remarks>
    public int HealthCheckTimeoutSeconds { get; set; } = 15;

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

    public GatewayForwardedHeadersOptions ForwardedHeaders { get; set; } = new();

    public OverviewOptions Overview { get; set; } = new();
}
