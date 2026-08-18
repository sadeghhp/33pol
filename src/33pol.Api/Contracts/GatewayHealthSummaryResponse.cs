namespace Pol33.Api.Contracts;

/// <summary>
/// The shape <c>/health</c> serves to callers who are not operators: overall status, counts, and
/// per-backend up/down — never the upstream URL or the prober's error text.
/// </summary>
/// <remarks>
/// A deliberately separate type rather than the full <c>GatewayHealthResponse</c> with fields
/// blanked, so the anonymous contract cannot regain a sensitive field by accident.
/// </remarks>
public sealed class GatewayHealthSummaryResponse
{
    public required string Status { get; init; }

    public required DateTimeOffset Uptime { get; init; }

    public int TotalBackends { get; init; }

    public int HealthyBackends { get; init; }

    public int UnhealthyBackends { get; init; }

    public IReadOnlyList<GatewayBackendHealthSummaryEntry> Backends { get; init; } = [];
}

public sealed class GatewayBackendHealthSummaryEntry
{
    public required string ModelId { get; init; }

    public bool IsHealthy { get; init; }

    public DateTimeOffset? LastChecked { get; init; }
}
