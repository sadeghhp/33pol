using Pol33.Core.Identity;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

internal static class IdentityEntityMapper
{
    public static TenantRecord ToRecord(TenantEntity entity) =>
        new(
            entity.Id,
            entity.Slug,
            entity.Name,
            entity.PlanSlug,
            entity.CostCenter,
            entity.IsActive,
            entity.CreatedAt,
            entity.UpdatedAt);

    public static TenantEntity ToEntity(TenantRecord record) =>
        new()
        {
            Id = record.Id,
            Slug = record.Slug,
            Name = record.Name,
            PlanSlug = record.PlanSlug,
            CostCenter = record.CostCenter,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

    public static ApiKeyRecord ToRecord(ApiKeyEntity entity) =>
        new(
            entity.Id,
            entity.TenantId,
            entity.KeyHash,
            entity.KeyPrefix,
            entity.Role,
            entity.Scopes,
            entity.ExpiresAt,
            entity.RevokedAt,
            entity.CreatedAt,
            entity.LastUsedAt,
            entity.Label,
            entity.Assignee,
            entity.Description,
            entity.CostCenter,
            entity.ArchivedAt);

    public static ApiKeyEntity ToEntity(ApiKeyRecord record) =>
        new()
        {
            Id = record.Id,
            TenantId = record.TenantId,
            KeyHash = record.KeyHash,
            KeyPrefix = record.KeyPrefix,
            Role = record.Role,
            Scopes = record.Scopes.ToList(),
            ExpiresAt = record.ExpiresAt,
            RevokedAt = record.RevokedAt,
            CreatedAt = record.CreatedAt,
            LastUsedAt = record.LastUsedAt,
            Label = record.Label,
            Assignee = record.Assignee,
            Description = record.Description,
            CostCenter = record.CostCenter,
            ArchivedAt = record.ArchivedAt,
        };

    public static ApiKeyLifecycleEventRecord ToRecord(ApiKeyLifecycleEventEntity entity) =>
        new(
            entity.Id,
            entity.ApiKeyId,
            entity.TenantId,
            entity.KeyPrefix,
            entity.Event,
            entity.OccurredAt,
            entity.Label,
            entity.ActorApiKeyId,
            entity.Reason,
            entity.HadUsage);

    public static ApiKeyLifecycleEventEntity ToEntity(ApiKeyLifecycleEventRecord record) =>
        new()
        {
            Id = record.Id,
            ApiKeyId = record.ApiKeyId,
            TenantId = record.TenantId,
            KeyPrefix = record.KeyPrefix,
            Label = record.Label,
            Event = record.Event,
            OccurredAt = record.OccurredAt,
            ActorApiKeyId = record.ActorApiKeyId,
            Reason = record.Reason,
            HadUsage = record.HadUsage,
        };

    public static ModelGrantRecord ToRecord(ModelGrantEntity entity) =>
        new(entity.Id, entity.TenantId, entity.ModelPattern, entity.Effect);

    public static ModelGrantEntity ToEntity(ModelGrantRecord record) =>
        new()
        {
            Id = record.Id,
            TenantId = record.TenantId,
            ModelPattern = record.ModelPattern,
            Effect = record.Effect,
        };

    public static ApiKeyModelGrantRecord ToRecord(ApiKeyModelGrantEntity entity) =>
        new(entity.Id, entity.ApiKeyId, entity.ModelPattern, entity.Effect);

    public static ApiKeyModelGrantEntity ToEntity(ApiKeyModelGrantRecord record) =>
        new()
        {
            Id = record.Id,
            ApiKeyId = record.ApiKeyId,
            ModelPattern = record.ModelPattern,
            Effect = record.Effect,
        };
}
