namespace Pol33.Core.Models;

/// <summary>
/// One operator-facing diagnostic event. Distinct from <see cref="RecentRequestEntry"/>, which
/// records that a request happened; a log entry records what went wrong and — where the gateway
/// can tell — what to do about it.
/// </summary>
/// <remarks>
/// A record so the store can produce an updated copy on coalesce instead of mutating an instance a
/// reader may already be serializing.
/// </remarks>
public sealed record GatewayLogEntry
{
    public required string Id { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Serialized <see cref="GatewayLogLevel"/> so the admin API stays string-typed.</summary>
    public required string Level { get; init; }

    /// <summary>Logger category or subsystem the event came from, e.g. <c>ModelTest</c>.</summary>
    public required string Category { get; init; }

    /// <summary>
    /// Stable machine-readable code for the failure shape (e.g. <c>upstream.http_404</c>), so an
    /// operator can search for recurrences without matching on prose.
    /// </summary>
    public string? EventCode { get; init; }

    public required string Message { get; init; }

    /// <summary>Raw supporting text — upstream body, exception detail. May be long; the UI collapses it.</summary>
    public string? Detail { get; init; }

    /// <summary>Actionable remediation the gateway is confident about. Null when it cannot tell.</summary>
    public string? Hint { get; init; }

    public string? ModelId { get; init; }

    public string? RequestId { get; init; }

    /// <summary>
    /// How many times this identical event has fired. The store coalesces repeats within a short
    /// window so one misconfigured upstream cannot evict every other diagnostic from the buffer.
    /// </summary>
    /// <remarks>
    /// Init-only, along with <see cref="LastTimestampUtc"/>, so the store replaces an entry on
    /// coalesce rather than mutating it. Both were settable, and the store updated them after
    /// handing the entry to a reader — a reader serializing a multi-word
    /// <see cref="DateTimeOffset"/> outside the lock could observe it half-written.
    /// </remarks>
    public int Repeats { get; init; } = 1;

    /// <summary>Timestamp of the most recent occurrence; equals <see cref="TimestampUtc"/> until a repeat lands.</summary>
    public DateTimeOffset LastTimestampUtc { get; init; }
}
