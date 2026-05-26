namespace Pol33.Core.RateLimiting;

public sealed record RateLimitPolicy(int Rpm, int Burst, int MaxConcurrentStreams)
{
    public static RateLimitPolicy Default { get; } = new(60, 10, 5);
}
