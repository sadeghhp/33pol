using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class NoOpBillingUsageService : IBillingUsageService
{
    public Task<UsageReportResponse> GetUsageReportAsync(
        UsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        return Task.FromResult(new UsageReportResponse
        {
            Rollups = Array.Empty<DailyUsageRollupRecord>(),
            Summary = new UsageReportSummary(),
        });
    }

    public UsageExportResult ExportRollups(IReadOnlyList<DailyUsageRollupRecord> rollups, string format) =>
        UsageExportFormatter.Format(rollups, format);

    public Task<UsageExportResult> ExportEventsAsync(
        BillingEventQuery query,
        string format,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(UsageExportFormatter.FormatEvents(
            Array.Empty<AdminBillingEventListItem>(), format, query.FromDate, query.ToDate, truncated: false));

    public Task<BillingEventsPage> QueryEventsAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = query;
        _ = cancellationToken;
        return Task.FromResult(new BillingEventsPage
        {
            Events = Array.Empty<AdminBillingEventListItem>(),
            Limit = 0,
        });
    }
}
