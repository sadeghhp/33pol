namespace Pol33.Persistence.Entities;

/// <summary>
/// One persisted gateway failure. Unlike the Logs buffer, these survive a restart — an incident
/// review the morning after is the main reason the Errors tab exists.
/// </summary>
public sealed class GatewayErrorEntity
{
    /// <summary>Surrogate key. Also the tiebreak for ordering occurrences within the same instant.</summary>
    public long Id { get; set; }

    /// <summary>The record's own <c>err_…</c> id, stable across the memory buffer and the database.</summary>
    public required string RecordId { get; set; }

    public required string Fingerprint { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string Level { get; set; }

    public required string Source { get; set; }

    public required string Category { get; set; }

    public string? EventCode { get; set; }

    public required string Message { get; set; }

    public string? ExceptionType { get; set; }

    public string? StackTrace { get; set; }

    public string? Method { get; set; }

    public string? Path { get; set; }

    public string? RouteKind { get; set; }

    public int StatusCode { get; set; }

    public string? ModelId { get; set; }

    public string? UpstreamTarget { get; set; }

    public string? Outcome { get; set; }

    public string? TenantId { get; set; }

    public string? ApiKeyId { get; set; }

    public string? RequestId { get; set; }

    public double? DurationMs { get; set; }

    public string? UpstreamBodySnippet { get; set; }

    public string? Hint { get; set; }
}
