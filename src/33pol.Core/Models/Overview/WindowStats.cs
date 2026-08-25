namespace Pol33.Core.Models.Overview;

/// <summary>
/// Aggregates over one trailing time window (<c>1m</c>, <c>5m</c>, <c>1h</c>, <c>24h</c>). Every
/// number here is computed from in-memory buckets and resets with the process — unlike the lifetime
/// counters on the summary, which are persisted.
/// </summary>
public sealed record WindowStats
{
    public required string Window { get; init; }

    public int WindowSeconds { get; init; }

    public long Requests { get; init; }

    public long Errors { get; init; }

    /// <summary>Errors ÷ requests, 0 when there were no requests.</summary>
    public double ErrorRate { get; init; }

    public double RequestsPerSecond { get; init; }

    public double LatencyAvgMs { get; init; }

    public double LatencyP50Ms { get; init; }

    public double LatencyP95Ms { get; init; }

    public double LatencyP99Ms { get; init; }

    /// <summary>Null when no streaming response produced a first token inside the window.</summary>
    public double? TtftP50Ms { get; init; }

    public double? TtftP95Ms { get; init; }

    public long TtftSamples { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    /// <summary>Sum of priced request costs; requests without a rate card contribute nothing.</summary>
    public decimal PricedCost { get; init; }

    public long PricedRequests { get; init; }

    /// <summary>Rejections keyed by <see cref="RejectionReasonExtensions.ToLabel"/>; zero entries omitted.</summary>
    public IReadOnlyDictionary<string, long> RejectionsByReason { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Per-model breakdown for the busiest models in the window (capped).</summary>
    public IReadOnlyList<WindowModelStats> PerModel { get; init; } = [];
}

public sealed record WindowModelStats
{
    public required string ModelId { get; init; }

    public long Requests { get; init; }

    public long Errors { get; init; }

    public double ErrorRate { get; init; }

    public double LatencyP95Ms { get; init; }

    public double? TtftP95Ms { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public decimal PricedCost { get; init; }
}
