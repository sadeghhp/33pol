namespace Pol33.Core.Models;

public sealed class GatewayHealthResponse
{
    public required string Status { get; init; }

    public required DateTimeOffset Uptime { get; init; }

    public int TotalBackends { get; init; }

    public int HealthyBackends { get; init; }

    public int UnhealthyBackends { get; init; }

    public IReadOnlyList<GatewayBackendHealthEntry> Backends { get; init; } = [];
}

public sealed class GatewayBackendHealthEntry
{
    public required string ModelId { get; init; }

    public required string Url { get; init; }

    public bool IsHealthy { get; init; }

    public DateTimeOffset? LastChecked { get; init; }

    public string? Error { get; init; }
}
