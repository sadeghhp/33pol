namespace Pol33.Core.Models;

/// <remarks>
/// A record rather than a class so the live feed can restamp an in-flight entry's elapsed duration
/// with <c>with</c> on every read, without mutating the shared instance other readers hold.
/// </remarks>
public sealed record RecentRequestEntry
{
    public required string RequestId { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public string? ModelId { get; init; }

    public string? TenantId { get; init; }

    public int StatusCode { get; init; }

    public double DurationMs { get; init; }

    public bool IsStreaming { get; init; }

    public string? ErrorCode { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// True while the request is still being forwarded. In-flight entries carry the elapsed time so
    /// far in <see cref="DurationMs"/> and a <see cref="StatusCode"/> of 0 — the upstream has not
    /// answered yet. They exist only in memory and are never persisted to a snapshot.
    /// </summary>
    /// <remarks>
    /// Without these the dashboard could not show an inference that was actually running: every
    /// counter and every feed row was written at completion, so a 60-second non-streaming call left
    /// the console reporting an idle gateway for its whole duration.
    /// </remarks>
    public bool IsInFlight { get; init; }
}
