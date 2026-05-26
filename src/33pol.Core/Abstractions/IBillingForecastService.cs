using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBillingForecastService
{
    Task<UsageForecastResponse> GetForecastAsync(
        Guid? tenantId,
        int trailingDays = 7,
        CancellationToken cancellationToken = default);
}
