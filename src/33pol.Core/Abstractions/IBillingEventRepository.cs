using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBillingEventRepository
{
    Task<bool> TryAppendAsync(BillingEventRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>> GetUsageSummariesAsync(
        Guid tenantId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);
}
