namespace Pol33.Core.RateLimiting;

/// <summary>
/// One rate-limit tier, applied per partition — a tenant, or a single client address for anonymous
/// traffic. Every API key belonging to a tenant draws on the same tier, so the numbers below are a
/// tenant-wide budget rather than a per-key one.
/// </summary>
/// <param name="Rpm">
/// Sustained requests per minute. The token bucket refills at <c>Rpm / 60</c> per second, so this is
/// the long-run ceiling. Must be at least 1; there is no "unlimited" value — use the gateway-wide
/// master switch to turn enforcement off.
/// </param>
/// <param name="Burst">
/// Extra tokens the bucket may hold above <paramref name="Rpm"/>, so capacity is
/// <c>Rpm + Burst</c>. This is what an idle partition may spend at once. Zero means no burst
/// allowance beyond one minute's worth of tokens.
/// </param>
/// <param name="MaxConcurrentStreams">
/// Streaming responses the partition may have open at once. <b>Zero means unlimited</b>, not
/// "streaming denied" — there is no per-tier way to refuse streaming, and a tier with the cap left
/// at zero is not throttled on concurrency at all.
/// </param>
public sealed record RateLimitPolicy(int Rpm, int Burst, int MaxConcurrentStreams)
{
    public static RateLimitPolicy Default { get; } = new(60, 10, 5);
}
