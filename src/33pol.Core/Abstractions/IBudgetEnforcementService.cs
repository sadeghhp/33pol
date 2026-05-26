using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBudgetEnforcementService
{
    ValueTask<BudgetCheckResult> CheckBeforeForwardAsync(
        string? tenantId,
        CancellationToken cancellationToken = default);
}
