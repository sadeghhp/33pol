using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class UsageReportResponse
{
    public required IReadOnlyList<DailyUsageRollupRecord> Rollups { get; init; }

    public required UsageReportSummary Summary { get; init; }

    /// <summary>ISO currency the costs are labelled in (the gateway's default billing currency).</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// <c>rollups</c> when read from the daily rollup table, <c>events</c> when aggregated from the
    /// ledger (per-key reports).
    /// </summary>
    public string Source { get; init; } = UsageReportSource.Rollups;

    /// <summary>
    /// Models present in <see cref="Rollups"/> that currently have no rate card. Their spend is
    /// recorded as zero, which is otherwise indistinguishable from genuinely free usage.
    /// </summary>
    public IReadOnlyList<string> UnpricedModelIds { get; init; } = [];
}

public static class UsageReportSource
{
    public const string Rollups = "rollups";
    public const string Events = "events";
}

public sealed class UsageReportSummary
{
    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public decimal TotalCost { get; init; }

    public int TotalRequests { get; init; }

    /// <summary>Requests in the report that carry no tenant (anonymous public-model traffic).</summary>
    public int AnonymousRequests { get; init; }
}

public sealed class UsageExportResult
{
    public required string ContentType { get; init; }

    public required string Body { get; init; }

    public required string FileName { get; init; }

    /// <summary>True when the export hit its row cap and rows were dropped.</summary>
    public bool Truncated { get; init; }
}
