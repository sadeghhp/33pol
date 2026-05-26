using Pol33.Core.Identity;

namespace Pol33.Core.Abstractions;

public interface ITenantRepository
{
    Task<TenantRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TenantRecord?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<TenantRecord> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default);
}

public sealed class CreateTenantRequest
{
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? PlanSlug { get; init; }

    public string? CostCenter { get; init; }

    public bool IsActive { get; init; } = true;
}
