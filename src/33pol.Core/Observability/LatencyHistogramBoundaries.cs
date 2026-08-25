namespace Pol33.Core.Observability;

/// <summary>
/// The fixed histogram boundaries shared by the OpenTelemetry exporter and the in-process windowed
/// statistics behind the admin Overview, so a p95 read on the dashboard and a p95 read in Grafana
/// come from the same buckets. Values are in milliseconds.
/// </summary>
public static class LatencyHistogramBoundaries
{
    /// <summary>Upstream round-trip duration, 50 ms → 10 min.</summary>
    public static readonly double[] DurationMs =
        [50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 30_000, 60_000, 120_000, 300_000, 600_000];

    /// <summary>Time to first token, 25 ms → 1 min.</summary>
    public static readonly double[] TimeToFirstTokenMs =
        [25, 50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 30_000, 60_000];

    /// <summary>The same boundaries in seconds, for the OpenTelemetry histogram views.</summary>
    public static double[] ToSeconds(double[] milliseconds) =>
        Array.ConvertAll(milliseconds, static ms => ms / 1_000d);
}
