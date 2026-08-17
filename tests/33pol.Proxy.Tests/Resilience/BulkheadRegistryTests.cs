using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Resilience;

public sealed class BulkheadRegistryTests
{
    [Fact]
    public async Task TryAcquireAsync_WithinLimit_ReturnsReleasableLease()
    {
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 2 },
            }),
            Substitute.For<IGatewayMetricsCollector>());

        var lease = await registry.TryAcquireAsync("m1", CancellationToken.None);
        lease.Should().NotBeNull();
        lease!.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_AtCapacity_ReturnsNull()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 1 },
            }),
            metrics);

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await registry.TryAcquireAsync("m1", CancellationToken.None);
        second.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m1");

        first!.Dispose();
        var third = await registry.TryAcquireAsync("m1", CancellationToken.None);
        third.Should().NotBeNull();
        third!.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTrackedModelLimitReached_RejectsNewModel()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions
                {
                    MaxConcurrentForwardsPerModel = 1,
                    MaxTrackedResilienceModels = 1,
                },
            }),
            metrics);

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var secondModel = await registry.TryAcquireAsync("m2", CancellationToken.None);
        secondModel.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m2");

        first!.Dispose();
    }
}

public sealed class BulkheadRegistryQueueTests
{
    private static BulkheadRegistry Create(int maxConcurrent, int maxQueued, int timeoutSeconds = 30, IGatewayMetricsCollector? metrics = null) =>
        new(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions
                {
                    MaxConcurrentForwardsPerModel = maxConcurrent,
                    MaxQueuedForwardsPerModel = maxQueued,
                    BulkheadQueueTimeoutSeconds = timeoutSeconds,
                },
            }),
            metrics ?? Substitute.For<IGatewayMetricsCollector>());

    /// <summary>
    /// A burst above the ceiling waits for a slot instead of being refused: the second caller is
    /// admitted the moment the first releases.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_AtCapacityWithQueue_WaitsForAReleasedSlot()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = Create(maxConcurrent: 1, maxQueued: 1, metrics: metrics);

        var first = await registry.TryAcquireAsync("m1", CancellationToken.None);
        first.Should().NotBeNull();

        var waiting = registry.TryAcquireAsync("m1", CancellationToken.None).AsTask();
        await Task.Delay(100);
        waiting.IsCompleted.Should().BeFalse("the queued request must wait, not be refused");
        metrics.Received(1).RecordBulkheadQueuedChange("m1", 1);

        first!.Dispose();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        second.Should().NotBeNull();
        metrics.Received(1).RecordBulkheadQueuedChange("m1", -1);
        metrics.DidNotReceive().RecordBulkheadRejection("m1");
        second!.Dispose();
    }

    /// <summary>The queue is bounded: past MaxQueued the gateway sheds load immediately.</summary>
    [Fact]
    public async Task TryAcquireAsync_QueueFull_RefusesImmediately()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = Create(maxConcurrent: 1, maxQueued: 1, metrics: metrics);

        var held = await registry.TryAcquireAsync("m1", CancellationToken.None);
        var queued = registry.TryAcquireAsync("m1", CancellationToken.None).AsTask();
        await Task.Delay(50);

        var overflow = await registry.TryAcquireAsync("m1", CancellationToken.None);
        overflow.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m1");

        held!.Dispose();
        (await queued.WaitAsync(TimeSpan.FromSeconds(5)))!.Dispose();
    }

    /// <summary>A waiter whose patience runs out is refused, and its queue place is handed back.</summary>
    [Fact]
    public async Task TryAcquireAsync_QueueTimeout_RefusesAndLeavesTheQueue()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = Create(maxConcurrent: 1, maxQueued: 1, timeoutSeconds: 1, metrics: metrics);

        var held = await registry.TryAcquireAsync("m1", CancellationToken.None);
        var timedOut = await registry.TryAcquireAsync("m1", CancellationToken.None);

        timedOut.Should().BeNull();
        metrics.Received(1).RecordBulkheadRejection("m1");
        metrics.Received(1).RecordBulkheadQueuedChange("m1", -1);

        // The place freed by the timeout is usable again.
        var again = registry.TryAcquireAsync("m1", CancellationToken.None).AsTask();
        await Task.Delay(50);
        again.IsCompleted.Should().BeFalse();
        held!.Dispose();
        (await again.WaitAsync(TimeSpan.FromSeconds(5)))!.Dispose();
    }

    /// <summary>A client that hangs up while queued leaves the queue rather than holding a place.</summary>
    [Fact]
    public async Task TryAcquireAsync_ClientAbortsWhileQueued_ThrowsAndLeavesTheQueue()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var registry = Create(maxConcurrent: 1, maxQueued: 1, metrics: metrics);
        using var aborted = new CancellationTokenSource();

        var held = await registry.TryAcquireAsync("m1", CancellationToken.None);
        var queued = registry.TryAcquireAsync("m1", aborted.Token).AsTask();
        await Task.Delay(50);
        aborted.Cancel();

        await queued.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        metrics.Received(1).RecordBulkheadQueuedChange("m1", -1);

        var overflow = registry.TryAcquireAsync("m1", CancellationToken.None).AsTask();
        await Task.Delay(50);
        overflow.IsCompleted.Should().BeFalse("the aborted waiter's place must be free again");
        held!.Dispose();
        (await overflow.WaitAsync(TimeSpan.FromSeconds(5)))!.Dispose();
    }

    /// <summary>With no queue configured, capacity is refused immediately (the historical contract).</summary>
    [Fact]
    public async Task TryAcquireAsync_NoQueueConfigured_RefusesAtCapacity()
    {
        var registry = Create(maxConcurrent: 1, maxQueued: 0);
        var held = await registry.TryAcquireAsync("m1", CancellationToken.None);
        (await registry.TryAcquireAsync("m1", CancellationToken.None)).Should().BeNull();
        held!.Dispose();
    }
}
