using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface ITenantRepository
{
    Task<TenantRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TenantRecord?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<TenantRecord> CreateAsync(TenantRecord tenant, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantRecord>> ListActiveAsync(CancellationToken cancellationToken = default);
}
