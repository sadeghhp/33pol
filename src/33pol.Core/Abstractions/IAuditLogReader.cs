namespace Pol33.Core.Abstractions;

/// <summary>Reads the admin audit trail back, newest first, for the Overview's activity card.</summary>
public interface IAuditLogReader
{
    /// <summary>True when a trail exists to read (the file has been created).</summary>
    bool IsAvailable { get; }

    Task<AuditLogReadResult> ReadRecentAsync(int limit, CancellationToken cancellationToken = default);
}

public sealed record AuditLogReadResult(IReadOnlyList<AuditLogEntryView> Entries, int ParseErrors, DateTimeOffset? NewestUtc);

/// <param name="Details">The action's details re-serialised as compact JSON; null when the record had none.</param>
public sealed record AuditLogEntryView(
    DateTimeOffset TimestampUtc,
    string Action,
    string? TenantId,
    string? ApiKeyId,
    string? Details);
