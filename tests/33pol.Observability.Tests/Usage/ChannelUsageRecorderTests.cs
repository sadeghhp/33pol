using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
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
        var persistence = Substitute.For<IUsagePersistenceHandler>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IUsagePersistenceHandler)).Returns(persistence);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var recorder = new ChannelUsageRecorder(quota, scopeFactory, metrics, NullLogger<ChannelUsageRecorder>.Instance);

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

        quota.Received(1).CommitUsage(
            "tenant-a", "gpt-4o", 15, "req-1", Arg.Any<DateTimeOffset?>());
        await persistence.Received(1).PersistAsync(Arg.Is<UsageEvent>(e => e.RequestId == "req-1"), Arg.Any<CancellationToken>());
        metrics.Received(1).RecordTokenUsage("gpt-4o", 10, 5);
    }

    [Fact]
    public async Task Enqueue_WhenSaturatedBeforeDraining_DropsNewestAndKeepsQueuedEvents()
    {
        const int channelCapacity = 10_000;
        var quota = Substitute.For<IQuotaService>();
        var persistence = Substitute.For<IUsagePersistenceHandler>();
        persistence.PersistAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IUsagePersistenceHandler)).Returns(persistence);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var recorder = new ChannelUsageRecorder(quota, scopeFactory, metrics, NullLogger<ChannelUsageRecorder>.Instance);

        // Fill beyond capacity BEFORE starting the reader: the overflow event is dropped on write
        // (Wait mode) rather than silently evicting the oldest already-queued event.
        for (var i = 0; i <= channelCapacity; i++)
        {
            recorder.Enqueue(new UsageEvent
            {
                RequestId = $"req-{i}",
                ModelId = "m1",
                PromptTokens = 1,
                CompletionTokens = 0,
                DurationMs = 1,
            });
        }

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        // Oldest queued event is retained and persisted...
        await persistence.Received().PersistAsync(
            Arg.Is<UsageEvent>(e => e.RequestId == "req-0"),
            Arg.Any<CancellationToken>());
        // ...and the overflow (newest) event was dropped, not silently swapped in.
        await persistence.DidNotReceive().PersistAsync(
            Arg.Is<UsageEvent>(e => e.RequestId == $"req-{channelCapacity}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenPersistThrows_ContinuesWithNextEvent()
    {
        var quota = Substitute.For<IQuotaService>();
        var persistence = Substitute.For<IUsagePersistenceHandler>();
        persistence.PersistAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("transient failure"),
                _ => ValueTask.CompletedTask);

        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IUsagePersistenceHandler)).Returns(persistence);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var recorder = new ChannelUsageRecorder(quota, scopeFactory, metrics, NullLogger<ChannelUsageRecorder>.Instance);

        recorder.Enqueue(new UsageEvent { RequestId = "req-throws", ModelId = "m1" });
        recorder.Enqueue(new UsageEvent { RequestId = "req-ok", ModelId = "m1" });

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        // A single failing event must not tear down the loop: both events are attempted.
        await persistence.Received(2).PersistAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }
}
