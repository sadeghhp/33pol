using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBudgetEnforcementService
{
    ValueTask<BudgetCheckResult> CheckBeforeForwardAsync(
        string? tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves an estimated maximum cost for an in-flight request against the tenant's hard-stop
    /// budgets before forwarding, so concurrent requests (whose actual cost is not yet known) cannot
    /// collectively overshoot a hard cap. Returns a non-allowed result (reserving nothing) when the
    /// reservation would breach a hard-stop budget. Every successful reservation must be released via
    /// <see cref="ReleaseReservation"/> once the request's actual cost has been recorded.
    /// </summary>
    ValueTask<BudgetCheckResult> TryReserveAsync(
        string? tenantId,
        string requestId,
        string canonicalModelId,
        long? requestedMaxTokens,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a reservation taken by <see cref="TryReserveAsync"/> (no-op if unknown).</summary>
    void ReleaseReservation(string requestId);
}
