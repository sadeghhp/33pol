namespace Pol33.Core.Models;

/// <summary>
/// What retention has removed from the error archive, kept so the Errors tab can state its own
/// coverage instead of leaving a shrinking count to be read as data going missing.
/// </summary>
public sealed record GatewayErrorRetentionState
{
    /// <summary>Occurrences deleted by retention since the archive was last cleared.</summary>
    public long PrunedTotal { get; init; }

    public DateTimeOffset? LastPrunedAtUtc { get; init; }

    /// <summary>Age cutoff applied by the last pass. Nothing older than this survives.</summary>
    public DateTimeOffset? RetainedSinceUtc { get; init; }
}
