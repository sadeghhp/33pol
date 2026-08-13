namespace Pol33.Core.Models;

/// <summary>
/// Recurring occurrences of one failure, collapsed into a single row for the admin Errors tab.
/// The grouped view is the default because an incident is almost always one fault repeating, and
/// a flat list of 4,000 identical rows buries every other fault that happened alongside it.
/// </summary>
public sealed record GatewayErrorGroup
{
    public required string Fingerprint { get; init; }

    /// <summary>Total occurrences within the queried window.</summary>
    public long Count { get; init; }

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }

    public string? EventCode { get; init; }

    public int StatusCode { get; init; }

    public string? ModelId { get; init; }

    public string? Method { get; init; }

    public string? Path { get; init; }

    public string? UpstreamTarget { get; init; }

    public string? Hint { get; init; }

    /// <summary>Request id of the most recent occurrence, for the cross-link into Recent requests.</summary>
    public string? LastRequestId { get; init; }

    /// <summary>The most recent occurrence, carrying the stack trace the detail panel renders.</summary>
    public GatewayErrorRecord? Sample { get; init; }
}
