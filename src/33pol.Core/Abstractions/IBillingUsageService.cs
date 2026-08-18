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

    /// <summary>
    /// Ledger export. Rows are capped at <see cref="UsageExportLimits.MaxEventRows"/>; the result
    /// says whether the cap was hit.
    /// </summary>
    Task<UsageExportResult> ExportEventsAsync(
        BillingEventQuery query,
        string format,
        CancellationToken cancellationToken = default);

    Task<BillingEventsPage> QueryEventsAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default);
}
