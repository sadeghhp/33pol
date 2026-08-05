using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

public sealed class GatewayRuntimeStateTests
{
    [Fact]
    public void EnqueueRecent_ExceedsMax_TrimsOldest()
    {
        var runtime = new GatewayRuntimeState { MaxRecentRequests = 2 };
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r2",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r3",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 200,
        });

        var recent = runtime.GetRecent(10);
        recent.Should().HaveCount(2);
        recent[0].RequestId.Should().Be("r3");
        recent[1].RequestId.Should().Be("r2");
    }

    [Fact]
    public void EnqueueRecent_PreservesErrorCode()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(new RecentRequestEntry
        {
            RequestId = "r1",
            Method = "POST",
            Path = "/v1/chat/completions",
            ModelId = "m1",
            StatusCode = 502,
            ErrorCode = "upstream_error",
        });

        runtime.GetRecent(1).Single().ErrorCode.Should().Be("upstream_error");
    }
}
