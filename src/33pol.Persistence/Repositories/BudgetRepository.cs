using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Persistence.Mapping;

namespace Pol33.Persistence.Repositories;

public sealed class BudgetRepository(GatewayDbContext dbContext) : IBudgetRepository
{
    public async Task<IReadOnlyList<BudgetRecord>> GetByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.Budgets
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(BillingEntityMapper.ToRecord).ToList();
    }
}
