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
}

public sealed class RateLimitTierOptions
{
    public int Rpm { get; set; } = 600;

    public int Burst { get; set; } = 100;

    public int MaxConcurrentStreams { get; set; } = 50;
}
