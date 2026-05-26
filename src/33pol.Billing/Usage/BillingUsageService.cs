using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingUsageService(
    IDailyUsageRollupRepository rollups,
    IBillingEventRepository billingEvents) : IBillingUsageService
{
    public async Task<UsageReportResponse> GetUsageReportAsync(
        UsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rollupRecords = await rollups
            .GetRollupsAsync(request.FromDate, request.ToDate, request.TenantId, cancellationToken)
            .ConfigureAwait(false);

        return new UsageReportResponse
        {
            Rollups = rollupRecords,
            Summary = new UsageReportSummary
            {
                TotalPromptTokens = rollupRecords.Sum(r => r.PromptTokens),
                TotalCompletionTokens = rollupRecords.Sum(r => r.CompletionTokens),
                TotalCost = rollupRecords.Sum(r => r.TotalCost),
                TotalRequests = rollupRecords.Sum(r => r.RequestCount),
            },
        };
    }

    public UsageExportResult ExportRollups(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        string format) =>
        UsageExportFormatter.Format(rollups, format);

    public async Task<BillingEventsPage> QueryEventsAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, 5000);
        var normalized = query with { Limit = limit };
        var events = await billingEvents.QueryAsync(normalized, cancellationToken).ConfigureAwait(false);
        return new BillingEventsPage { Events = events, Limit = limit };
    }
}
