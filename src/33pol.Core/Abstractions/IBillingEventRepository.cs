using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IBillingEventRepository
{
    Task<bool> TryAppendAsync(BillingEventRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default);
}
