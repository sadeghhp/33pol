using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

/// <summary>Wire shape for one row of the admin Logs tab.</summary>
public sealed class AdminLogEntryDto
{
    public required string Id { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public DateTimeOffset LastTimestampUtc { get; init; }

    public required string Level { get; init; }

    public required string Category { get; init; }

    public string? EventCode { get; init; }

    public required string Message { get; init; }

    public string? Detail { get; init; }

    public string? Hint { get; init; }

    public string? ModelId { get; init; }

    public string? RequestId { get; init; }

    public int Repeats { get; init; }

    public static AdminLogEntryDto From(GatewayLogEntry entry) => new()
    {
        Id = entry.Id,
        TimestampUtc = entry.TimestampUtc,
        LastTimestampUtc = entry.LastTimestampUtc == default ? entry.TimestampUtc : entry.LastTimestampUtc,
        Level = entry.Level,
        Category = entry.Category,
        EventCode = entry.EventCode,
        Message = entry.Message,
        Detail = entry.Detail,
        Hint = entry.Hint,
        ModelId = entry.ModelId,
        RequestId = entry.RequestId,
        Repeats = entry.Repeats < 1 ? 1 : entry.Repeats,
    };
}

public sealed class AdminLogListResponse
{
    public required IReadOnlyList<AdminLogEntryDto> Entries { get; init; }

    /// <summary>Rows returned after filtering — not the buffer's total size.</summary>
    public int Count { get; init; }

    /// <summary>
    /// Rows matching the filter before <c>limit</c> was applied. Without this the UI cannot
    /// distinguish "200 of 741 matches" from "200 of 200", and its truncation warning fires
    /// whenever a page happens to be exactly full.
    /// </summary>
    public int Total { get; init; }

    /// <summary>Ring-buffer capacity, so the UI can say what the operator is not seeing.</summary>
    public int Capacity { get; init; }
}
