namespace Pol33.Core.Models.Overview;

/// <summary>Health of the usage/billing pipeline behind every cost figure the console shows.</summary>
public sealed record PipelineOverview
{
    /// <summary>Usage events waiting for the batch writer; -1 when unknown.</summary>
    public int UsageWriterQueueDepth { get; init; } = -1;

    public int UsageWriterCapacity { get; init; }

    /// <summary>Events that never reached the ledger since process start.</summary>
    public long UsageWriterDropped { get; init; }

    public long UsageParseFailures { get; init; }

    public long EstimatedUsage { get; init; }

    public long UnsplitUsage { get; init; }
}
