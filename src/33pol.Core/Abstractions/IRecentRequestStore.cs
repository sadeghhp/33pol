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

    /// <summary>
    /// Attaches the priced usage of a request to its feed row. Called by the billing pipeline once
    /// the usage event has been written, which is one flush interval after the request completed —
    /// so this must find the row whether it is still in flight, already completed, or (in the rare
    /// race where pricing wins) not yet recorded at all. Unknown ids are retained briefly so a
    /// completion that lands afterwards still picks the usage up.
    /// </summary>
    void AttachUsage(string requestId, RecentRequestUsage usage);

    IReadOnlyList<RecentRequestEntry> GetRecent(int limit);
}
