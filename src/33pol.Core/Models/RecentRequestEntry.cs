namespace Pol33.Core.Models;

public sealed class RecentRequestEntry
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
}
