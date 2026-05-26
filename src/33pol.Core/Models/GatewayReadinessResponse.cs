namespace Pol33.Core.Models;

public sealed class GatewayReadinessResponse
{
    public required string Status { get; init; }

    public bool RegistryLoaded { get; init; }

    public int ModelCount { get; init; }

    public int HealthyBackends { get; init; }

    public bool IsDraining { get; init; }
}
