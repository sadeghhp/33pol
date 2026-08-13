namespace Pol33.Core.Models;

/// <summary>Where a page of errors was served from, so the UI can say so when there is no database.</summary>
public static class GatewayErrorSources
{
    public const string Database = "database";
    public const string Memory = "memory";
}

/// <summary>A page of individual error occurrences.</summary>
public sealed record GatewayErrorPage
{
    public IReadOnlyList<GatewayErrorRecord> Items { get; init; } = [];

    /// <summary>
    /// Rows matching the filter <em>before</em> <see cref="Limit"/> is applied. The Logs tab's
    /// truncation hint is wrong today precisely because its equivalent field is the page length.
    /// </summary>
    public long Total { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    public string Source { get; init; } = GatewayErrorSources.Memory;
}

/// <summary>A page of grouped errors.</summary>
public sealed record GatewayErrorGroupPage
{
    public IReadOnlyList<GatewayErrorGroup> Items { get; init; } = [];

    /// <summary>Distinct groups matching the filter, before <see cref="Limit"/>.</summary>
    public long Total { get; init; }

    /// <summary>Total occurrences across all matching groups, for the "N occurrences in M groups" header.</summary>
    public long OccurrenceTotal { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    public string Source { get; init; } = GatewayErrorSources.Memory;
}
