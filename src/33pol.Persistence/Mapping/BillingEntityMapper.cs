using Pol33.Core.Billing;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

internal static class BillingEntityMapper
{
    public static RateCardRecord ToRecord(RateCardEntity entity) =>
        new(
            entity.Id,
            entity.Slug,
            entity.Name,
            entity.ModelId,
            entity.InputPricePerMillionTokens,
            entity.OutputPricePerMillionTokens,
            entity.Currency,
            entity.EffectiveFrom,
            entity.EffectiveUntil,
            entity.IsActive,
            entity.CreatedAt,
            entity.UpdatedAt);

    public static PlanRecord ToRecord(PlanEntity entity) =>
        new(
            entity.Id,
            entity.Slug,
            entity.Name,
            entity.RateCardSlug,
            entity.MonthlyTokenLimit,
            entity.RequestsPerMinute,
            entity.ConcurrencyLimit,
            entity.IsActive,
            entity.CreatedAt,
            entity.UpdatedAt);

    public static BudgetRecord ToRecord(BudgetEntity entity) =>
        new(
            entity.Id,
            entity.TenantId,
            entity.Name,
            entity.AmountLimit,
            entity.Currency,
            entity.WarningThresholdRatio,
            entity.HardStopEnabled,
            entity.PeriodStartDay,
            entity.CreatedAt,
            entity.UpdatedAt);

    public static BillingEventRecord ToRecord(BillingEventEntity entity) =>
        new(
            entity.Id,
            entity.RequestId,
            entity.TenantId,
            entity.ApiKeyId,
            entity.ModelId,
            entity.CostCenter,
            entity.PromptTokens,
            entity.CompletionTokens,
            entity.InputCost,
            entity.OutputCost,
            entity.TotalCost,
            entity.DurationMs,
            entity.RecordedAt);

    public static RateCardEntity ToEntity(RateCardRecord record) =>
        new()
        {
            Id = record.Id,
            Slug = record.Slug,
            Name = record.Name,
            ModelId = record.ModelId,
            InputPricePerMillionTokens = record.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = record.OutputPricePerMillionTokens,
            Currency = record.Currency,
            EffectiveFrom = record.EffectiveFrom,
            EffectiveUntil = record.EffectiveUntil,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

    public static PlanEntity ToEntity(PlanRecord record) =>
        new()
        {
            Id = record.Id,
            Slug = record.Slug,
            Name = record.Name,
            RateCardSlug = record.RateCardSlug,
            MonthlyTokenLimit = record.MonthlyTokenLimit,
            RequestsPerMinute = record.RequestsPerMinute,
            ConcurrencyLimit = record.ConcurrencyLimit,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

    public static BudgetEntity ToEntity(BudgetRecord record) =>
        new()
        {
            Id = record.Id,
            TenantId = record.TenantId,
            Name = record.Name,
            AmountLimit = record.AmountLimit,
            Currency = record.Currency,
            WarningThresholdRatio = record.WarningThresholdRatio,
            HardStopEnabled = record.HardStopEnabled,
            PeriodStartDay = record.PeriodStartDay,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

    public static BillingEventEntity ToEntity(BillingEventRecord record) =>
        new()
        {
            Id = record.Id,
            RequestId = record.RequestId,
            TenantId = record.TenantId,
            ApiKeyId = record.ApiKeyId,
            ModelId = record.ModelId,
            CostCenter = record.CostCenter,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            InputCost = record.InputCost,
            OutputCost = record.OutputCost,
            TotalCost = record.TotalCost,
            DurationMs = record.DurationMs,
            RecordedAt = record.RecordedAt,
        };
}
