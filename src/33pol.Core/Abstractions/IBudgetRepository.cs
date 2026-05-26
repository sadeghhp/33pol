using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IBudgetRepository
{
    Task<IReadOnlyList<BudgetRecord>> GetByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
