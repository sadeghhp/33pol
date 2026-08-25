namespace Pol33.Core.Models.Overview;

/// <summary>Recent admin actions read back from the audit trail.</summary>
public sealed record ActivityOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    public IReadOnlyList<ActivityEntry> Entries { get; init; } = [];

    /// <summary>Audit lines that could not be parsed while reading; non-zero means the file was tampered with or truncated.</summary>
    public int ParseErrors { get; init; }

    public bool Available { get; init; }
}

public sealed record ActivityEntry
{
    public DateTimeOffset TimestampUtc { get; init; }

    public required string Action { get; init; }

    public string? TenantId { get; init; }

    public string? TenantSlug { get; init; }

    public string? ApiKeyId { get; init; }

    public string? ApiKeyLabel { get; init; }

    /// <summary>The action's details, re-serialised as compact JSON.</summary>
    public string? Details { get; init; }
}
