namespace Pol33.Core.Models.Overview;

/// <summary>
/// One row of the Overview's routing-health card: probe health, circuit breaker and bulkhead
/// occupancy for a registered model, plus its 5-minute error rate and p95 from the windows.
/// </summary>
public sealed record BackendOverview
{
    public required string ModelId { get; init; }

    public required string Url { get; init; }

    public string? Alias { get; init; }

    public bool IsHealthy { get; init; }

    public int? StatusCode { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset? LastCheckedUtc { get; init; }

    public DateTimeOffset? LastTransitionUtc { get; init; }

    /// <summary><c>closed</c> | <c>half_open</c> | <c>open</c> | <c>unknown</c> (no breaker allocated yet).</summary>
    public string CircuitState { get; init; } = "unknown";

    public DateTimeOffset? CircuitOpenedAt { get; init; }

    public int CircuitFailures { get; init; }

    public int CircuitOutcomes { get; init; }

    public DateTimeOffset? CircuitLastTransitionUtc { get; init; }

    public int InFlight { get; init; }

    public int Queued { get; init; }

    public int MaxConcurrent { get; init; }

    public int MaxQueued { get; init; }

    public long Requests5m { get; init; }

    public double? ErrorRate5m { get; init; }

    public double? LatencyP95Ms5m { get; init; }
}
