using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingBudgetEnforcementService(IServiceScopeFactory scopeFactory) : IBudgetEnforcementService
{
    public async ValueTask<BudgetCheckResult> CheckBeforeForwardAsync(
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tenantId, out var parsedTenantId))
        {
            return BudgetCheckResult.Allowed;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var budgets = scope.ServiceProvider.GetService<IBudgetRepository>();
        var rollups = scope.ServiceProvider.GetService<IDailyUsageRollupRepository>();
        if (budgets is null || rollups is null)
        {
            return BudgetCheckResult.Allowed;
        }

        var tenantBudgets = await budgets
            .GetByTenantAsync(parsedTenantId, cancellationToken)
            .ConfigureAwait(false);

        var hardBudgets = tenantBudgets.Where(b => b.HardStopEnabled).ToList();
        if (hardBudgets.Count == 0)
        {
            return BudgetCheckResult.Allowed;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var budget in hardBudgets)
        {
            var periodStart = BillingUsagePersistenceHandler.GetPeriodStart(today, budget.PeriodStartDay);
            var periodRollups = await rollups
                .GetRollupsAsync(periodStart, today, parsedTenantId, cancellationToken)
                .ConfigureAwait(false);

            var spend = periodRollups.Sum(r => r.TotalCost);
            if (spend >= budget.AmountLimit)
            {
                return BudgetCheckResult.HardExceeded(budget.Name);
            }
        }

        return BudgetCheckResult.Allowed;
    }
}
