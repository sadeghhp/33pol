namespace Pol33.Core.Configuration;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

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
    public int Rpm { get; set; } = 60;

    public int Burst { get; set; } = 10;

    public int MaxConcurrentStreams { get; set; } = 5;
}
