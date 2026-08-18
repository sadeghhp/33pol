using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Observability.Diagnostics;

namespace Pol33.Observability.Tests.Diagnostics;

public sealed class GatewayErrorBatchPersistenceHandlerTests
{
    /// <summary>
    /// The archive is slowest exactly during an error storm. A size-triggered flush that cannot
    /// take the flush gate must leave its records in the bounded pending list instead of spawning
    /// another drained batch queued behind the semaphore — otherwise memory grows with error rate
    /// times DB stall, not with MaxPending.
    /// </summary>
    [Fact]
    public async Task Enqueue_WhileAFlushIsStalled_KeepsRecordsInBoundedPendingInsteadOfQueueingBatches()
    {
        var archive = new BlockingArchive();
        var handler = CreateHandler(archive, batchSize: 5);

        // First batch takes the gate and stalls inside the archive.
        for (var i = 0; i < 5; i++)
        {
            handler.Enqueue(Error($"req_{i}"));
        }

        await archive.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handler.PendingCount.Should().Be(0);

        // Storm: 40 batches worth of records arrive while the archive is stuck.
        for (var i = 0; i < 200; i++)
        {
            handler.Enqueue(Error($"storm_{i}"));
        }

        // Nothing else reached the archive, and the buffer is bounded by MaxPending (10 x batch).
        archive.Batches.Should().HaveCount(1);
        handler.PendingCount.Should().Be(50);

        // Once the archive recovers, the timer path drains the survivors in one write.
        archive.Release.SetResult();
        await handler.FlushPendingAsync();
        archive.Batches.Should().HaveCount(2);
        archive.Batches[1].Should().HaveCount(50);
        archive.Batches[1][0].RequestId.Should().Be("storm_150", "the oldest records were trimmed, not the newest");
    }

    [Fact]
    public async Task DiscardPending_AfterABatchWasDrained_PreventsItFromLandingInTheArchive()
    {
        // Drained by the size trigger, then wiped before the write completes.
        var gated = new BlockingArchive();
        var handler2 = CreateHandler(gated, batchSize: 1);
        handler2.Enqueue(Error("req_a"));
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gated.Batches.Should().HaveCount(1);

        handler2.Enqueue(Error("req_b"));   // gate busy: stays pending
        handler2.DiscardPending();          // wipe: pending gone, generation bumped
        gated.Release.SetResult();
        await handler2.FlushPendingAsync();

        gated.Batches.Should().HaveCount(1, "req_b was discarded before any write and must not appear");
    }

    private static GatewayErrorBatchPersistenceHandler CreateHandler(IGatewayErrorArchive archive, int batchSize)
    {
        var services = new ServiceCollection();
        services.AddSingleton(archive);
        var provider = services.BuildServiceProvider();
        var options = new GatewayErrorTrackingOptions { WriterBatchSize = batchSize, WriterFlushIntervalMs = 60_000 };
        return new GatewayErrorBatchPersistenceHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<GatewayErrorBatchPersistenceHandler>.Instance);
    }

    private static GatewayErrorRecord Error(string requestId) => new()
    {
        Id = $"err_{Guid.NewGuid():N}",
        Fingerprint = "fp",
        OccurredAt = DateTimeOffset.UtcNow,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "Test",
        Message = "boom",
        RequestId = requestId,
    };

    private sealed class BlockingArchive : IGatewayErrorArchive
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyList<GatewayErrorRecord>> Batches { get; } = [];

        public async Task AppendBatchAsync(IReadOnlyList<GatewayErrorRecord> batch, CancellationToken cancellationToken = default)
        {
            lock (Batches)
            {
                Batches.Add(batch);
            }

            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        public Task<GatewayErrorPage> QueryAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GatewayErrorGroupPage> QueryGroupsAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GatewayErrorFacets> GetFacetsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> PruneAsync(DateTimeOffset olderThan, int maxRows, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
