using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Usage;

namespace Pol33.Observability.Tests.Usage;

public sealed class ChannelUsageRecorderTests
{
    [Fact]
    public async Task Enqueue_ProcessesEventAndCommitsQuota()
    {
        var quota = Substitute.For<IQuotaService>();
        var recorder = new ChannelUsageRecorder(quota, NullLogger<ChannelUsageRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        recorder.Enqueue(new UsageEvent
        {
            RequestId = "req-1",
            TenantId = "tenant-a",
            ModelId = "gpt-4o",
            PromptTokens = 10,
            CompletionTokens = 5,
        });

        await Task.Delay(200);
        await recorder.StopAsync(CancellationToken.None);

        quota.Received(1).CommitUsage("tenant-a", "gpt-4o", 15, "req-1");
    }

    [Fact]
    public void TryParseUsage_InvalidJson_ReturnsFalse()
    {
        UsageJsonParser.TryParseUsage("{not-json"u8.ToArray(), out _, out _).Should().BeFalse();
    }
}
