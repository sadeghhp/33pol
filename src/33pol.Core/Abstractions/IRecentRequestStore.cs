using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IRecentRequestStore
{
    void Record(RecentRequestEntry entry);

    IReadOnlyList<RecentRequestEntry> GetRecent(int limit);
}
