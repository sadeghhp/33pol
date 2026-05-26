using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IRateCardRepository
{
    Task<RateCardRecord?> GetActiveForModelAsync(
        string modelId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);
}
