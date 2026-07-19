namespace Pol33.Persistence.Entities;

/// <summary>
/// Single-row table holding the runtime-tunable quota scalars: the default per-partition monthly
/// token limit and the soft-warning ratio. Seeded once from appsettings, then the database is truth.
/// </summary>
public sealed class QuotaSettingsEntity
{
    /// <summary>Fixed singleton key (always 1).</summary>
    public int Id { get; set; }

    public long DefaultMonthlyTokenLimit { get; set; }

    /// <summary>Stored as TEXT on SQLite (exact) and converted to the double on QuotaOptions at load.</summary>
    public decimal SoftLimitRatio { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
