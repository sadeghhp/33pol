namespace Pol33.Core.Models.Overview;

/// <summary>
/// A short server-side time series (one point per minute, oldest first) for the Overview sparklines,
/// so every operator sees the same trend and it survives a page reload.
/// </summary>
public sealed record OverviewSeries
{
    public DateTimeOffset StartUtc { get; init; }

    public int StepSeconds { get; init; } = 60;

    public IReadOnlyList<OverviewSeriesPoint> Points { get; init; } = [];
}

public sealed record OverviewSeriesPoint
{
    public DateTimeOffset T { get; init; }

    public long Requests { get; init; }

    public long Errors { get; init; }

    public double LatencyP95Ms { get; init; }

    public double? TtftP95Ms { get; init; }

    /// <summary>Peak in-flight requests sampled during the minute.</summary>
    public int InFlight { get; init; }

    public long Tokens { get; init; }

    public decimal Cost { get; init; }
}
