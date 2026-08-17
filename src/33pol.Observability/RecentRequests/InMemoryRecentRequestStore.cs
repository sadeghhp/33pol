using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.RecentRequests;

public sealed class InMemoryRecentRequestStore(GatewayRuntimeState runtimeState) : IRecentRequestStore
{
    public void Record(RecentRequestEntry entry) => runtimeState.EnqueueRecent(entry);

    public void BeginInFlight(RecentRequestEntry entry) => runtimeState.BeginInFlight(entry);

    public void CompleteInFlight(string requestId) => runtimeState.CompleteInFlight(requestId);

    public void AttachUsage(string requestId, RecentRequestUsage usage) => runtimeState.AttachUsage(requestId, usage);

    public IReadOnlyList<RecentRequestEntry> GetRecent(int limit) => runtimeState.GetRecent(limit);
}
