using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IRecentRequestStore
{
    void Record(RecentRequestEntry entry);

    /// <summary>
    /// Publishes a request to the live feed as soon as forwarding starts, so the console shows it
    /// while it runs. <see cref="Record"/> for the same request id replaces this entry; callers must
    /// still call <see cref="CompleteInFlight"/> on every exit path so an abandoned request cannot
    /// linger in the feed forever.
    /// </summary>
    void BeginInFlight(RecentRequestEntry entry);

    /// <summary>Removes an in-flight entry. Idempotent, and a no-op for an unknown request id.</summary>
    void CompleteInFlight(string requestId);

    IReadOnlyList<RecentRequestEntry> GetRecent(int limit);
}
