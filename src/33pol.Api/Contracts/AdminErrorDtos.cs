using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

/// <summary>One recurring failure, as rendered by a row of the admin Errors tab.</summary>
public sealed class AdminErrorGroupDto
{
    public required string Fingerprint { get; init; }

    public long Count { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; }

    public DateTimeOffset LastSeenUtc { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }

    public string? ErrorCode { get; init; }

    public int StatusCode { get; init; }

    public string? ModelId { get; init; }

    public string? EndpointMethod { get; init; }

    public string? EndpointPath { get; init; }

    public string? UpstreamTarget { get; init; }

    public string? Hint { get; init; }

    public string? LastRequestId { get; init; }

    /// <summary>Stack trace of the most recent occurrence, so the row expands without a second fetch.</summary>
    public string? StackTrace { get; init; }

    public string? UpstreamBodySnippet { get; init; }

    public static AdminErrorGroupDto From(GatewayErrorGroup group) => new()
    {
        Fingerprint = group.Fingerprint,
        Count = group.Count,
        FirstSeenUtc = group.FirstSeen,
        LastSeenUtc = group.LastSeen,
        Level = group.Level,
        Message = group.Message,
        ExceptionType = group.ExceptionType,
        ErrorCode = group.EventCode,
        StatusCode = group.StatusCode,
        ModelId = group.ModelId,
        EndpointMethod = group.Method,
        EndpointPath = group.Path,
        UpstreamTarget = group.UpstreamTarget,
        Hint = group.Hint,
        LastRequestId = group.LastRequestId,
        StackTrace = group.Sample?.StackTrace,
        UpstreamBodySnippet = group.Sample?.UpstreamBodySnippet,
    };
}

/// <summary>One individual occurrence, as shown in a group's drill-down.</summary>
public sealed class AdminErrorOccurrenceDto
{
    public required string Id { get; init; }

    public required string Fingerprint { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public required string Level { get; init; }

    public required string Source { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }

    public string? StackTrace { get; init; }

    public string? EndpointMethod { get; init; }

    public string? EndpointPath { get; init; }

    public int StatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public string? Outcome { get; init; }

    public string? ModelId { get; init; }

    public string? UpstreamTarget { get; init; }

    public string? TenantId { get; init; }

    public string? ApiKeyId { get; init; }

    public string? RequestId { get; init; }

    public double? DurationMs { get; init; }

    public string? UpstreamBodySnippet { get; init; }

    public string? Hint { get; init; }

    public static AdminErrorOccurrenceDto From(GatewayErrorRecord record) => new()
    {
        Id = record.Id,
        Fingerprint = record.Fingerprint,
        TimestampUtc = record.OccurredAt,
        Level = record.Level,
        Source = record.Source,
        Category = record.Category,
        Message = record.Message,
        ExceptionType = record.ExceptionType,
        StackTrace = record.StackTrace,
        EndpointMethod = record.Method,
        EndpointPath = record.Path,
        StatusCode = record.StatusCode,
        ErrorCode = record.EventCode,
        Outcome = record.Outcome,
        ModelId = record.ModelId,
        UpstreamTarget = record.UpstreamTarget,
        TenantId = record.TenantId,
        ApiKeyId = record.ApiKeyId,
        RequestId = record.RequestId,
        DurationMs = record.DurationMs,
        UpstreamBodySnippet = record.UpstreamBodySnippet,
        Hint = record.Hint,
    };
}

public sealed class AdminErrorGroupListResponse
{
    public required IReadOnlyList<AdminErrorGroupDto> Groups { get; init; }

    /// <summary>Distinct groups matching the filter, before paging.</summary>
    public long Total { get; init; }

    /// <summary>Occurrences across all matching groups.</summary>
    public long OccurrenceTotal { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    /// <summary><c>database</c> or <c>memory</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// False when the rows in this response are held in memory only and will not survive a restart.
    /// </summary>
    /// <remarks>
    /// Derived from where the page was actually served from, not from how the store is configured.
    /// A database-backed store that fell back to its in-memory buffer after a failed query is
    /// serving volatile data, and saying otherwise would put "kept across restarts" above rows that
    /// are not.
    /// </remarks>
    public bool Persisted { get; init; }

    public static AdminErrorGroupListResponse From(GatewayErrorGroupPage page, bool persisted) => new()
    {
        Groups = [.. page.Items.Select(AdminErrorGroupDto.From)],
        Total = page.Total,
        OccurrenceTotal = page.OccurrenceTotal,
        Limit = page.Limit,
        Offset = page.Offset,
        Source = page.Source,
        Persisted = persisted && page.Source == GatewayErrorSources.Database,
    };
}

public sealed class AdminErrorListResponse
{
    public required IReadOnlyList<AdminErrorOccurrenceDto> Occurrences { get; init; }

    /// <summary>Occurrences matching the filter, before paging.</summary>
    public long Total { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    public required string Source { get; init; }

    public bool Persisted { get; init; }

    public static AdminErrorListResponse From(GatewayErrorPage page, bool persisted) => new()
    {
        Occurrences = [.. page.Items.Select(AdminErrorOccurrenceDto.From)],
        Total = page.Total,
        Limit = page.Limit,
        Offset = page.Offset,
        Source = page.Source,
        Persisted = persisted && page.Source == GatewayErrorSources.Database,
    };
}

public sealed class AdminErrorFacetValueDto
{
    public required string Value { get; init; }

    public long Count { get; init; }

    public static AdminErrorFacetValueDto From(GatewayErrorFacetValue value) => new()
    {
        Value = value.Value,
        Count = value.Count,
    };
}

public sealed class AdminErrorFacetsDto
{
    public required IReadOnlyList<AdminErrorFacetValueDto> Models { get; init; }

    public required IReadOnlyList<AdminErrorFacetValueDto> StatusCodes { get; init; }

    public required IReadOnlyList<AdminErrorFacetValueDto> ErrorCodes { get; init; }

    public required IReadOnlyList<AdminErrorFacetValueDto> Levels { get; init; }

    public static AdminErrorFacetsDto From(GatewayErrorFacets facets) => new()
    {
        Models = [.. facets.Models.Select(AdminErrorFacetValueDto.From)],
        StatusCodes = [.. facets.Statuses.Select(AdminErrorFacetValueDto.From)],
        ErrorCodes = [.. facets.Codes.Select(AdminErrorFacetValueDto.From)],
        Levels = [.. facets.Levels.Select(AdminErrorFacetValueDto.From)],
    };
}

public sealed class AdminErrorClearResponse
{
    public bool Success { get; init; }

    public required string Message { get; init; }

    public int RecordsDeleted { get; init; }

    public int RecentRequestRowsRemoved { get; init; }

    public long TotalErrorsCleared { get; init; }

    /// <summary>
    /// False means the persisted counter snapshot was left alone, so a restart would restore the
    /// old totals. Surfaced rather than hidden: a clear that silently does not stick is worse than
    /// one that reports its limits.
    /// </summary>
    public bool SnapshotRewritten { get; init; }

    public bool DatabaseAvailable { get; init; }

    public static AdminErrorClearResponse From(GatewayErrorClearResult result) => new()
    {
        Success = true,
        Message = result.DatabaseAvailable
            ? "Errors cleared and the persisted counter snapshot was rewritten."
            : "Errors cleared from memory. No database is configured, so nothing was persisted.",
        RecordsDeleted = result.RecordsDeleted,
        RecentRequestRowsRemoved = result.RecentRequestRowsRemoved,
        TotalErrorsCleared = result.TotalErrorsCleared,
        SnapshotRewritten = result.SnapshotRewritten,
        DatabaseAvailable = result.DatabaseAvailable,
    };
}
