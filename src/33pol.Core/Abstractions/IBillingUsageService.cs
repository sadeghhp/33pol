using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBillingUsageService
{
    Task<UsageReportResponse> GetUsageReportAsync(
        UsageReportRequest request,
        CancellationToken cancellationToken = default);

    UsageExportResult ExportRollups(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        string format);

    Task<BillingEventsPage> QueryEventsAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default);
}
