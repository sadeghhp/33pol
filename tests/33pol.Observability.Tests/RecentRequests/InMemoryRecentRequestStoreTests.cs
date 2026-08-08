using Pol33.Core.Models;
using Pol33.Observability.RecentRequests;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.RecentRequests;

public sealed class InMemoryRecentRequestStoreTests
{
    [Fact]
    public void Record_AndGetRecent_RoundTripsViaRuntimeState()
    {
        var runtime = new GatewayRuntimeState();
        var store = new InMemoryRecentRequestStore(runtime);
        var entry = new RecentRequestEntry
        {
            RequestId = "req-1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        store.Record(entry);

        store.GetRecent(10).Should().ContainSingle()
            .Which.RequestId.Should().Be("req-1");
    }
}
