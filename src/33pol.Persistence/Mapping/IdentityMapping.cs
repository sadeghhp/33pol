using Pol33.Core.Identity;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

internal static class IdentityMapping
{
    public static TenantRecord ToRecord(this TenantEntity entity) =>
        new()
        {
            Id = entity.Id,
            Slug = entity.Slug,
            Name = entity.Name,
            PlanSlug = entity.PlanSlug,
            CostCenter = entity.CostCenter,
            IsActive = entity.IsActive,
        };

    public static ApiKeyRecord ToRecord(this ApiKeyEntity entity) =>
        new()
        {
            Id = entity.Id,
            TenantId = entity.TenantId,
            TenantSlug = entity.Tenant.Slug,
            Role = entity.Role,
            KeyHash = entity.KeyHash,
            KeyPrefix = entity.KeyPrefix,
            Scopes = entity.Scopes,
            ExpiresAt = entity.ExpiresAt,
            RevokedAt = entity.RevokedAt,
        };
}
