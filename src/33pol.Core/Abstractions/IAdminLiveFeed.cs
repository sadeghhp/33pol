namespace Pol33.Core.Abstractions;

/// <summary>
/// Change token for the admin console's live view. The version advances whenever the summary
/// counters or the recent-request feed change (a request admitted, completed, rejected or priced),
/// so a streaming endpoint can push a frame on change instead of on a timer.
/// </summary>
public interface IAdminLiveFeed
{
    /// <summary>Monotonically increasing; compare, never interpret.</summary>
    long Version { get; }
}
