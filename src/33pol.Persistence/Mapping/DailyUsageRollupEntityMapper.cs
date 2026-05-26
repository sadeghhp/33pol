using Pol33.Core.Billing;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

internal static class DailyUsageRollupEntityMapper
{
    public static DailyUsageRollupRecord ToRecord(DailyUsageRollupEntity entity) =>
        new(
            entity.UsageDate,
            entity.TenantId,
            entity.ModelId,
            entity.CostCenter,
            entity.PromptTokens,
            entity.CompletionTokens,
            entity.TotalCost,
            entity.RequestCount);

    public static DailyUsageRollupEntity ToEntity(DailyUsageRollupRecord record, DateTimeOffset updatedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            UsageDate = record.UsageDate,
            TenantId = record.TenantId,
            ModelId = record.ModelId,
            CostCenter = record.CostCenter,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            TotalCost = record.TotalCost,
            RequestCount = record.RequestCount,
            UpdatedAt = updatedAt,
        };

    public static void ApplyRecord(DailyUsageRollupEntity entity, DailyUsageRollupRecord record, DateTimeOffset updatedAt)
    {
        entity.PromptTokens = record.PromptTokens;
        entity.CompletionTokens = record.CompletionTokens;
        entity.TotalCost = record.TotalCost;
        entity.RequestCount = record.RequestCount;
        entity.UpdatedAt = updatedAt;
    }
}
