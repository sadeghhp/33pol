using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class UsageReportResponse
{
    public required IReadOnlyList<DailyUsageRollupRecord> Rollups { get; init; }

    public required UsageReportSummary Summary { get; init; }
}

public sealed class UsageReportSummary
{
    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public decimal TotalCost { get; init; }

    public int TotalRequests { get; init; }
}

public sealed class UsageExportResult
{
    public required string ContentType { get; init; }

    public required string Body { get; init; }

    public required string FileName { get; init; }
}
