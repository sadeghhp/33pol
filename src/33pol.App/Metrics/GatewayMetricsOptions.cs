namespace Pol33.App.Metrics;

/// <summary>
/// Access control for the Prometheus scrape endpoint (<c>/metrics</c>). Bound from
/// <c>Gateway:Metrics</c>; the token is conventionally supplied as the environment variable
/// <c>Gateway__Metrics__ScrapeToken</c> (the compose stack maps <c>GATEWAY_METRICS_SCRAPE_TOKEN</c>
/// onto it).
/// </summary>
/// <remarks>
/// The exposition carries a <c>model</c> label on request, error, latency, stream and token series,
/// so an anonymous scrape enumerates the model inventory and the traffic profile — the same data
/// <c>/stats</c> was moved behind the Operator policy to protect. A scrape therefore has to present
/// either an Operator API key or the dedicated token, unless the operator explicitly opts the
/// endpoint back to anonymous.
/// </remarks>
public sealed class GatewayMetricsOptions
{
    public const string SectionName = "Gateway:Metrics";

    /// <summary>
    /// Shared secret a scraper presents as <c>Authorization: Bearer &lt;token&gt;</c>. Empty means
    /// no token is accepted; an Operator API key still works.
    /// </summary>
    public string? ScrapeToken { get; set; }

    /// <summary>
    /// Serve <c>/metrics</c> without any credential. Off by default; turn on only when the port is
    /// reachable solely from the scraper's network.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public bool HasScrapeToken => !string.IsNullOrWhiteSpace(ScrapeToken);
}
