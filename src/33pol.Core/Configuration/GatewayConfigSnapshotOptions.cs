namespace Pol33.Core.Configuration;

/// <summary>
/// Tunables for the database-backed configuration snapshot syncer.
/// </summary>
public sealed class GatewayConfigSnapshotOptions
{
    public const string SectionName = "Gateway:ConfigSnapshot";

    /// <summary>
    /// How often the reconcile poll checks the config version for out-of-band changes. The primary
    /// refresh path is an in-process call after an admin write, so this can be relaxed.
    /// </summary>
    public int ReloadIntervalSeconds { get; set; } = 5;

    /// <summary>Upper bound on the exponential backoff between initial-load retries at startup.</summary>
    public int InitialLoadMaxBackoffSeconds { get; set; } = 30;
}
