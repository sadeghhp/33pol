using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Billing.Forecast;

public sealed class NoOpBillingForecastService : IBillingForecastService
{
    public Task<UsageForecastResponse> GetForecastAsync(
        Guid? tenantId,
        int trailingDays = 7,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new UsageForecastResponse
        {
            TrailingDays = trailingDays,
            TrailingTotalCost = 0m,
            ProjectedMonthlyCost = 0m,
            Currency = "USD",
        });
}
