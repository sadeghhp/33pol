using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Usage;

public sealed class BillingUsageService(
    IDailyUsageRollupRepository rollups,
    IBillingEventRepository billingEvents,
    IApiKeyRepository apiKeys) : IBillingUsageService
{
    public async Task<UsageReportResponse> GetUsageReportAsync(
        UsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rollupRecords = await rollups
            .GetRollupsAsync(request.FromDate, request.ToDate, request.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.CostCenter))
        {
            var costCenter = request.CostCenter.Trim();
            rollupRecords = rollupRecords
                .Where(r => string.Equals(r.CostCenter, costCenter, StringComparison.Ordinal))
                .ToList();
        }

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
        var enriched = await AdminBillingEventMapper
            .EnrichAsync(events, apiKeys, cancellationToken)
            .ConfigureAwait(false);
        return new BillingEventsPage { Events = enriched, Limit = limit };
    }
}
