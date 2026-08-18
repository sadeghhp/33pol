using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Forecast;

/// <summary>
/// Projects month-end spend as <c>month-to-date + average of the last N complete UTC days × days
/// remaining</c>.
/// </summary>
/// <remarks>
/// The previous shape — trailing N days <em>including today</em>, divided by N, times days in month —
/// diluted the average by whatever fraction of today had not happened yet, and ignored what the
/// month had actually cost so far. Both windows here honour the same scope, cost-centre and key
/// filters as the report they sit next to, so the number is comparable to the totals on screen.
/// </remarks>
public sealed class BillingForecastService(
    IDailyUsageRollupRepository rollups,
    IBillingEventRepository billingEvents,
    IOptions<BillingOptions> options,
    TimeProvider? timeProvider = null) : IBillingForecastService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<UsageForecastResponse> GetForecastAsync(
        UsageForecastRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trailingDays = Math.Clamp(request.TrailingDays, 1, 90);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var yesterday = today.AddDays(-1);
        var windowStart = today.AddDays(-trailingDays);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var daysRemaining = daysInMonth - today.Day;

        // One read covering both windows: from the earlier of (window start, month start) to today.
        var from = windowStart < monthStart ? windowStart : monthStart;
        var records = await LoadAsync(request, from, today, cancellationToken).ConfigureAwait(false);

        var trailingTotal = records
            .Where(r => r.UsageDate >= windowStart && r.UsageDate <= yesterday)
            .Sum(r => r.TotalCost);
        var monthToDate = records
            .Where(r => r.UsageDate >= monthStart && r.UsageDate <= today)
            .Sum(r => r.TotalCost);

        var avgDaily = trailingTotal / trailingDays;
        var projected = monthToDate + avgDaily * daysRemaining;

        return new UsageForecastResponse
        {
            TrailingDays = trailingDays,
            WindowStart = windowStart,
            WindowEnd = yesterday,
            TrailingTotalCost = trailingTotal,
            AverageDailyCost = decimal.Round(avgDaily, 6),
            MonthToDateCost = monthToDate,
            DaysRemainingInMonth = daysRemaining,
            ProjectedMonthlyCost = decimal.Round(projected, 4),
            Currency = options.Value.DefaultCurrency,
        };
    }

    private async Task<IReadOnlyList<DailyUsageRollupRecord>> LoadAsync(
        UsageForecastRequest request,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (request.ApiKeyId is Guid apiKeyId)
        {
            return await billingEvents
                .AggregateDailyAsync(
                    new BillingEventQuery(
                        from,
                        to,
                        request.Scope.TenantId,
                        apiKeyId,
                        request.CostCenter,
                        IncludeAnonymous: request.Scope.IncludeAnonymous,
                        NoCostCenter: request.NoCostCenter),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var records = await rollups
            .GetScopedRollupsAsync(request.Scope, from, to, cancellationToken)
            .ConfigureAwait(false);

        if (request.NoCostCenter)
        {
            return records.Where(r => string.IsNullOrWhiteSpace(r.CostCenter)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.CostCenter))
        {
            var wanted = request.CostCenter.Trim();
            return records
                .Where(r => string.Equals(r.CostCenter?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return records;
    }
}
