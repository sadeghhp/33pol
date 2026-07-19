namespace Pol33.Persistence.Entities;

/// <summary>
/// Single-row table holding the monotonic configuration version. A bump signals that some
/// database-backed config section changed, which the snapshot syncer detects on its reconcile poll.
/// </summary>
public sealed class ConfigVersionEntity
{
    /// <summary>Fixed singleton key (always 1).</summary>
    public int Id { get; set; }

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
