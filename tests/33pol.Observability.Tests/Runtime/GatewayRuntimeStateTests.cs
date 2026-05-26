using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

public sealed class GatewayRuntimeStateTests
{
    [Fact]
    public void EnqueueRecent_ExceedsMax_TrimsOldest()
    {
        var runtime = new GatewayRuntimeState { MaxRecentRequests = 2 };
        runtime.EnqueueRecent(new RecentRequestEntry { RequestId = "r1", ModelId = "m1", StatusCode = 200 });
        runtime.EnqueueRecent(new RecentRequestEntry { RequestId = "r2", ModelId = "m1", StatusCode = 200 });
        runtime.EnqueueRecent(new RecentRequestEntry { RequestId = "r3", ModelId = "m1", StatusCode = 200 });

        var recent = runtime.GetRecent(10);
        recent.Should().HaveCount(2);
        recent[0].RequestId.Should().Be("r3");
        recent[1].RequestId.Should().Be("r2");
    }

    [Fact]
    public void TryCommitQuota_DuplicateRequestId_ReturnsFalseOnSecondCommit()
    {
        var runtime = new GatewayRuntimeState();
        runtime.TryCommitQuota("req-1").Should().BeTrue();
        runtime.TryCommitQuota("req-1").Should().BeFalse();
    }

    [Fact]
    public void AddQuotaUsage_AccumulatesPerPartition()
    {
        var runtime = new GatewayRuntimeState();
        runtime.AddQuotaUsage("tenant-a", 100);
        runtime.AddQuotaUsage("tenant-a", 50);

        runtime.GetQuotaUsage("tenant-a").Should().Be(150);
    }
}
