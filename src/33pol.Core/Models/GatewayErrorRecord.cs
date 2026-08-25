namespace Pol33.Core.Models;

/// <summary>
/// Capture points that produce error records. Part of the fingerprint, so the same failure seen by
/// two of them groups as two rows rather than one double-counted total.
/// </summary>
public static class GatewayErrorSourceNames
{
    /// <summary>The inference proxy, which knows the model, upstream and outcome.</summary>
    public const string Proxy = "proxy";

    /// <summary>The terminal exception handler, covering admin and non-inference routes.</summary>
    public const string Exception = "exception";

    /// <summary>An <c>ILogger</c> call at Error or above, mirrored by the admin log sink.</summary>
    public const string Log = "log";

    /// <summary>The admin model-test probe.</summary>
    public const string ModelTest = "modeltest";

    /// <summary>Background health: a backend probe transitioning to unhealthy, or a credential that no longer decrypts.</summary>
    public const string Health = "health";
}

/// <summary>
/// One recorded gateway failure, with everything an operator needs to troubleshoot it without
/// leaving the console: what failed, where, for whom, and what the exception said.
/// </summary>
/// <remarks>
/// A record, and immutable, on purpose. <see cref="GatewayLogEntry"/> is a mutable class whose
/// repeat counters are updated in place, which means a reader can observe a half-written entry
/// once serialization happens outside the store's lock. Error records are never mutated after
/// construction, so every reader sees a complete object and the store needs no defensive copy.
/// </remarks>
public sealed record GatewayErrorRecord
{
    /// <summary>Stable id of this single occurrence, <c>err_{guid:N}</c>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Groups occurrences of the same underlying failure. Computed by
    /// <see cref="Diagnostics.GatewayErrorFingerprint"/>; never set by call sites.
    /// </summary>
    public required string Fingerprint { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Serialized <see cref="GatewayLogLevel"/>, so the wire format stays string-typed.</summary>
    public required string Level { get; init; }

    /// <summary>
    /// Which capture point produced this — <c>proxy</c>, <c>exception</c>, <c>log</c> or
    /// <c>modeltest</c>. Part of the fingerprint, so the same failure seen by two capture points
    /// shows as two groups rather than one inflated count.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Logger category or subsystem, namespace stripped.</summary>
    public required string Category { get; init; }

    /// <summary>Machine-readable failure shape, e.g. <c>upstream_error</c>.</summary>
    public string? EventCode { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }

    /// <summary>Redacted and length-capped stack trace. Null when the failure carried no exception.</summary>
    public string? StackTrace { get; init; }

    public string? Method { get; init; }

    public string? Path { get; init; }

    /// <summary>
    /// Coarse classification of <see cref="Path"/> (e.g. <c>chat</c>, <c>embeddings</c>, <c>admin</c>).
    /// The fingerprint uses this rather than the raw path, which would otherwise shatter a single
    /// failure into one group per distinct path variant.
    /// </summary>
    public string? RouteKind { get; init; }

    /// <summary>HTTP status the caller received. Zero when the failure was not HTTP-shaped.</summary>
    public int StatusCode { get; init; }

    public string? ModelId { get; init; }

    /// <summary>Upstream base URL with userinfo and query stripped. Never carries credentials.</summary>
    public string? UpstreamTarget { get; init; }

    /// <summary>How the request ended, e.g. <c>upstream_5xx</c>, <c>upstream_timeout</c>, <c>bulkhead_full</c>.</summary>
    public string? Outcome { get; init; }

    public string? TenantId { get; init; }

    /// <summary>The API key's identifier claim. Never the key itself.</summary>
    public string? ApiKeyId { get; init; }

    /// <summary>Correlates with the Recent requests feed and the Logs tab.</summary>
    public string? RequestId { get; init; }

    public double? DurationMs { get; init; }

    /// <summary>Redacted, length-capped upstream error body. Captured only when explicitly enabled.</summary>
    public string? UpstreamBodySnippet { get; init; }

    /// <summary>Actionable remediation from <see cref="Diagnostics.GatewayLogHints"/>, or null when the gateway cannot tell.</summary>
    public string? Hint { get; init; }
}
