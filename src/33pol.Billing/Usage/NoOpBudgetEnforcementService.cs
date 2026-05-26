using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class NoOpBudgetEnforcementService : IBudgetEnforcementService
{
    public ValueTask<BudgetCheckResult> CheckBeforeForwardAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        _ = tenantId;
        _ = cancellationToken;
        return ValueTask.FromResult(BudgetCheckResult.Allowed);
    }
}
