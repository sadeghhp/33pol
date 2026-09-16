namespace Pol33.Core.Models;

public sealed class GatewayReadinessResponse
{
    public required string Status { get; init; }

    public bool RegistryLoaded { get; init; }

    public int ModelCount { get; init; }

    /// <summary>Routes an operator has not stopped — the ones readiness is judged on.</summary>
    public int ConfiguredBackends { get; init; }

    /// <summary>
    /// Enabled routes the health sweep has actually reached a verdict on. The gap between this and
    /// <see cref="ConfiguredBackends"/> is what distinguishes "still warming up" from "down":
    /// before the first sweep it is 0, and readiness is withheld rather than assumed.
    /// </summary>
    public int ProbedBackends { get; init; }

    public int HealthyBackends { get; init; }

    public bool IsDraining { get; init; }
}
