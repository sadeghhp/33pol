namespace Pol33.Core.Configuration;

/// <summary>
/// Controls how often the in-memory dashboard counters and monthly quota usage are snapshotted to
/// the database. A shorter interval reduces how much is lost on an ungraceful kill; a longer one
/// reduces write volume.
/// </summary>
public sealed class GatewayStatsPersistenceOptions
{
    public const string SectionName = "Gateway:StatsPersistence";

    /// <summary>How often the background loop flushes the snapshot to the database.</summary>
    public int FlushIntervalSeconds { get; set; } = 10;
}
