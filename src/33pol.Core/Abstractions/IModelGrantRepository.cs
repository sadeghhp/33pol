using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface IModelGrantRepository
{
    Task<IReadOnlyList<ModelGrantRecord>> ListByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<ModelGrantRecord> AddAsync(ModelGrantRecord grant, CancellationToken cancellationToken = default);

    Task ReplaceForTenantAsync(
        Guid tenantId,
        IReadOnlyList<string> modelPatterns,
        CancellationToken cancellationToken = default);
}
