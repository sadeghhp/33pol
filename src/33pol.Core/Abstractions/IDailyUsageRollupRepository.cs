using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IDailyUsageRollupRepository
{
    Task<IReadOnlyList<DailyUsageRollupRecord>> GetRollupsAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    Task UpsertRollupsAsync(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        CancellationToken cancellationToken = default);
}
