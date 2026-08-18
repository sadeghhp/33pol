using Pol33.Core.Billing;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

/// <summary>
/// Maps rollup records to/from the storage shape. The table stores sentinels rather than NULLs in
/// the two nullable key columns — <see cref="AnonymousTenantId"/> for anonymous traffic and
/// <c>""</c> for "no cost centre" — because SQLite treats NULLs as distinct in UNIQUE indexes, which
/// left those buckets unprotected against duplicate rows. Normalized on write, denormalized on read,
/// so callers keep seeing <c>null</c>.
/// </summary>
internal static class DailyUsageRollupEntityMapper
{
    /// <summary>Stored tenant id for anonymous (no tenant) buckets.</summary>
    public static readonly Guid AnonymousTenantId = Guid.Empty;

    /// <summary>Stored cost centre for "no cost centre" buckets.</summary>
    public const string NoCostCenter = "";

    public static Guid ToStoredTenantId(Guid? tenantId) => tenantId ?? AnonymousTenantId;

    public static Guid? FromStoredTenantId(Guid tenantId) => tenantId == AnonymousTenantId ? null : tenantId;

    public static string ToStoredCostCenter(string? costCenter) =>
        DailyUsageRollupKey.NormalizeCostCenter(costCenter) ?? NoCostCenter;

    public static string? FromStoredCostCenter(string costCenter) =>
        DailyUsageRollupKey.NormalizeCostCenter(costCenter);

    public static DailyUsageRollupRecord ToRecord(DailyUsageRollupEntity entity) =>
        new(
            entity.UsageDate,
            FromStoredTenantId(entity.TenantId),
            entity.ModelId,
            FromStoredCostCenter(entity.CostCenter),
            entity.PromptTokens,
            entity.CompletionTokens,
            entity.TotalCost,
            entity.RequestCount);

    public static DailyUsageRollupEntity ToEntity(DailyUsageRollupRecord record, DateTimeOffset updatedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            UsageDate = record.UsageDate,
            TenantId = ToStoredTenantId(record.TenantId),
            ModelId = record.ModelId,
            CostCenter = ToStoredCostCenter(record.CostCenter),
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
