using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBillingForecastService
{
    Task<UsageForecastResponse> GetForecastAsync(
        UsageForecastRequest request,
        CancellationToken cancellationToken = default);
}
