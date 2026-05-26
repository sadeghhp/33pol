using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Forecast;

public sealed class BillingForecastService(
    IDailyUsageRollupRepository rollups,
    IOptions<BillingOptions> options) : IBillingForecastService
{
    public async Task<UsageForecastResponse> GetForecastAsync(
        Guid? tenantId,
        int trailingDays = 7,
        CancellationToken cancellationToken = default)
    {
        trailingDays = Math.Clamp(trailingDays, 1, 90);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-trailingDays + 1);

        var records = await rollups
            .GetRollupsAsync(from, today, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var trailingTotal = records.Sum(r => r.TotalCost);
        var avgDaily = trailingTotal / trailingDays;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var projectedMonthly = avgDaily * daysInMonth;

        return new UsageForecastResponse
        {
            TrailingDays = trailingDays,
            TrailingTotalCost = trailingTotal,
            ProjectedMonthlyCost = decimal.Round(projectedMonthly, 4),
            Currency = options.Value.DefaultCurrency,
        };
    }
}
