namespace Pol33.Core.Configuration;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Seed value for the global rate-limiting master switch. Only used to populate a fresh database;
    /// once seeded the live value comes from the config snapshot and is edited via the admin UI.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public RateLimitTierOptions Default { get; set; } = new();

    public Dictionary<string, RateLimitTierOptions> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RateLimitTierOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int InMemoryPartitionRetentionSeconds { get; set; } = 3600;

    public int InMemoryCompactionEveryOperations { get; set; } = 256;

    /// <summary>
    /// Shortest interval between two compaction sweeps. A sweep walks every live partition, on the
    /// request thread that triggered it, so the operation counter alone is not a bound: under load
    /// that is a full scan several times a second. Time-boxing it keeps the cost proportional to
    /// wall-clock rather than to traffic. A sweep forced by
    /// <see cref="InMemoryMaxPartitions"/> ignores this.
    /// </summary>
    public int InMemoryCompactionMinIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Hard ceiling on live partitions per dimension (request buckets, stream-slot states). Anonymous
    /// traffic partitions by client address, so without a ceiling a caller spread across an address
    /// range mints one entry per address and holds it for the whole retention window. When the
    /// ceiling is passed, a sweep runs immediately and, if that is not enough, the
    /// least-recently-seen partitions are evicted down to the ceiling. Evicting a partition resets
    /// its bucket, so keep this comfortably above the number of clients you expect.
    /// </summary>
    public int InMemoryMaxPartitions { get; set; } = 50_000;
}

public sealed class RateLimitTierOptions
{
    public int Rpm { get; set; } = 600;

    public int Burst { get; set; } = 100;

    /// <summary>Concurrent streaming responses allowed per partition. Zero means unlimited.</summary>
    public int MaxConcurrentStreams { get; set; } = 50;
}
