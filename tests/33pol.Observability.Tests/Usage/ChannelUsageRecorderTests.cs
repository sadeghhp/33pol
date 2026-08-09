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

    /// <summary>
    /// Anonymous usage must be committed under the same per-address partition the quota admission
    /// check reads. The old literal-"anonymous" fallback was a bucket no check consulted, so keyless
    /// callers of public models were never held to the monthly token quota.
    /// </summary>
    [Fact]
    public async Task Enqueue_AnonymousEventWithQuotaPartition_CommitsUnderThatPartition()
    {
        var quota = Substitute.For<IQuotaService>();
        var persistence = Substitute.For<IUsagePersistenceHandler>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IUsagePersistenceHandler)).Returns(persistence);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var recorder = new ChannelUsageRecorder(
            quota,
            scopeFactory,
            Substitute.For<IGatewayMetricsCollector>(),
            NullLogger<ChannelUsageRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        recorder.Enqueue(new UsageEvent
        {
            RequestId = "req-anon",
            TenantId = null,
            QuotaPartition = "anon:203.0.113.10",
            ModelId = "local-mock",
            PromptTokens = 7,
            CompletionTokens = 3,
        });

        await Task.Delay(200);
        await recorder.StopAsync(CancellationToken.None);

        quota.Received(1).CommitUsage(
            "anon:203.0.113.10", "local-mock", 10, "req-anon", Arg.Any<DateTimeOffset?>());
    }

    /// <summary>
    /// The batch persistence handler stops before this recorder does (reverse registration order),
    /// so events the shutdown drain delivers land in a buffer with no flush loop. The recorder must
    /// explicitly flush after draining, or the final partial batch is lost at process exit.
    /// </summary>
    [Fact]
    public async Task StopAsync_AfterDraining_FlushesThePersistenceHandler()
    {
        var quota = Substitute.For<IQuotaService>();
        var persistence = Substitute.For<IUsagePersistenceHandler>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IUsagePersistenceHandler)).Returns(persistence);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateAsyncScope().Returns(scope);

        var recorder = new ChannelUsageRecorder(
            quota,
            scopeFactory,
            Substitute.For<IGatewayMetricsCollector>(),
            NullLogger<ChannelUsageRecorder>.Instance);

        await recorder.StartAsync(CancellationToken.None);
        recorder.Enqueue(new UsageEvent
        {
            RequestId = "req-final",
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
        });

        await recorder.StopAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            persistence.PersistAsync(Arg.Is<UsageEvent>(e => e.RequestId == "req-final"), Arg.Any<CancellationToken>());
            persistence.FlushPendingAsync(Arg.Any<CancellationToken>());
        });
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
