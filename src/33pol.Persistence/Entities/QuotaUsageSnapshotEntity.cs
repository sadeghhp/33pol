namespace Pol33.Persistence.Entities;

/// <summary>
/// Durable per-partition monthly quota usage, keyed by partition. Distinct from
/// <see cref="QuotaUsageEntity"/> (the billing/phase-4 table not wired to the enforcement path);
/// this row backs the in-memory quota enforcement counter so it survives container recreation.
/// </summary>
public sealed class QuotaUsageSnapshotEntity
{
    public required string PartitionKey { get; set; }

    /// <summary>The billing month the usage applies to, formatted <c>yyyy-MM</c> (UTC).</summary>
    public required string Period { get; set; }

    public long Used { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
