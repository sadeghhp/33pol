using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pol33.Billing.Aggregates;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// A batch that fails to persist used to be logged and discarded — invisible to reconciliation,
/// because both the ledger and the rollups miss it. It must be retried, and only dropped (loudly,
/// with a counter) once retries are exhausted or the buffer cap is hit.
/// </summary>
public sealed class BillingUsageBatchPersistenceHandlerRetryTests
{
    [Fact]
    public async Task FailedBatch_IsRequeuedAndPersistedOnceTheStoreRecovers()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var billingEvents = Substitute.For<IBillingEventRepository>();
        var failing = true;
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => failing ? throw new InvalidOperationException("database is locked") : true);

        var handler = CreateHandler(billingEvents, batchSize: 1, flushIntervalMs: 60_000, clock: () => now);
        await handler.StartAsync(CancellationToken.None);

        await handler.PersistAsync(CreateEvent("req-1")); // size trigger -> fails -> re-queued
        handler.DroppedEventCount.Should().Be(0);

        failing = false;
        now = now.AddSeconds(5); // past the retry back-off
        await handler.PersistAsync(CreateEvent("req-2")); // size trigger -> flushes [req-1, req-2]

        var appended = billingEvents.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBillingEventRepository.TryAppendAsync))
            .Select(c => ((BillingEventRecord)c.GetArguments()[0]!).RequestId)
            .ToList();
        appended.Should().Contain("req-2");
        appended.Count(id => id == "req-1").Should().Be(2, "the first attempt failed and the retry succeeded");
        handler.DroppedEventCount.Should().Be(0);

        await handler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedBatch_IsNotRetriedBeforeTheBackoffElapses()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var handler = CreateHandler(billingEvents, batchSize: 1, flushIntervalMs: 60_000, clock: () => now);
        await handler.StartAsync(CancellationToken.None);

        await handler.PersistAsync(CreateEvent("req-1"));
        await handler.PersistAsync(CreateEvent("req-2")); // same instant: back-off holds, no second attempt

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
        handler.DroppedEventCount.Should().Be(0);
    }

    [Fact]
    public async Task Batch_IsDroppedAndCounted_OnceRetriesAreExhausted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var metrics = Substitute.For<IGatewayMetricsCollector>();

        var handler = CreateHandler(
            billingEvents, batchSize: 1, flushIntervalMs: 60_000, maxRetries: 1, metrics: metrics, clock: () => now);
        await handler.StartAsync(CancellationToken.None);

        await handler.PersistAsync(CreateEvent("req-1")); // attempt 1 fails -> re-queued
        handler.DroppedEventCount.Should().Be(0);

        now = now.AddMinutes(1);
        await handler.FlushPendingAsync(); // attempt 2 fails -> retries exhausted -> dropped

        handler.DroppedEventCount.Should().Be(1);
        metrics.Received(1).RecordUsageEventsDropped(1);

        // Nothing left to flush: the dropped batch is not retried forever.
        await handler.FlushPendingAsync();
        await billingEvents.Received(2).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Buffer_ShedsOldestEventsPastTheCap_WhilePersistenceIsFailing()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var metrics = Substitute.For<IGatewayMetricsCollector>();

        var handler = CreateHandler(
            billingEvents, batchSize: 1, flushIntervalMs: 60_000, maxPending: 2, metrics: metrics, clock: () => now);
        await handler.StartAsync(CancellationToken.None);

        await handler.PersistAsync(CreateEvent("req-1")); // fails, re-queued; back-off now blocks flushes
        await handler.PersistAsync(CreateEvent("req-2"));
        await handler.PersistAsync(CreateEvent("req-3")); // over cap: req-1 shed
        await handler.PersistAsync(CreateEvent("req-4")); // over cap: req-2 shed

        handler.DroppedEventCount.Should().Be(2);
        metrics.Received(2).RecordUsageEventsDropped(1);
    }

    /// <summary>
    /// The host's shutdown token is typically already cancelled when StopAsync runs. The final
    /// flush must not be skipped because of it.
    /// </summary>
    [Fact]
    public async Task StopAsync_WithAlreadyCancelledHostToken_StillFlushesPendingEvents()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = CreateHandler(billingEvents, batchSize: 100, flushIntervalMs: 60_000);
        await handler.StartAsync(CancellationToken.None);
        await handler.PersistAsync(CreateEvent("req-last"));

        await handler.StopAsync(new CancellationToken(canceled: true));

        await billingEvents.Received(1).TryAppendAsync(
            Arg.Is<BillingEventRecord>(r => r.RequestId == "req-last"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushCancelledMidWrite_RequeuesInsteadOfDropping()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<CancellationToken>().IsCancellationRequested
                ? throw new OperationCanceledException(call.Arg<CancellationToken>())
                : true);

        var handler = CreateHandler(billingEvents, batchSize: 1, flushIntervalMs: 60_000);

        await handler.PersistAsync(CreateEvent("req-1"), new CancellationToken(canceled: true));
        handler.DroppedEventCount.Should().Be(0);

        await handler.FlushPendingAsync();

        await billingEvents.Received(1).TryAppendAsync(
            Arg.Is<BillingEventRecord>(r => r.RequestId == "req-1"),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    private static BillingUsageBatchPersistenceHandler CreateHandler(
        IBillingEventRepository billingEvents,
        int batchSize,
        int flushIntervalMs,
        int maxRetries = 5,
        int maxPending = 10_000,
        IGatewayMetricsCollector? metrics = null,
        Func<DateTimeOffset>? clock = null)
    {
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var services = new ServiceCollection();
        services.AddScoped<BillingUsagePersistenceHandler>();
        services.AddSingleton(billingEvents);
        services.AddSingleton(rollups);
        services.AddSingleton<IDailyUsageRollupAggregator, DailyUsageRollupAggregator>();
        services.AddSingleton<IRateCardRepository>(Substitute.For<IRateCardRepository>());
        services.AddSingleton<IRateCardCostCalculator, RateCardCostCalculator>();
        services.AddSingleton<IBudgetRepository>(Substitute.For<IBudgetRepository>());
        services.AddSingleton<IBillingWebhookDispatcher>(Substitute.For<IBillingWebhookDispatcher>());
        services.AddSingleton<BillingBudgetWarningTracker>();
        services.AddSingleton<BillingUnpricedModelTracker>();
        services.AddLogging();
        services.AddSingleton<IApiKeyLastUsedTracker>(Substitute.For<IApiKeyLastUsedTracker>());
        services.AddSingleton(new BudgetReservationLedger(TimeSpan.FromMinutes(2)));

        var provider = services.BuildServiceProvider();
        var options = Options.Create(new BillingOptions
        {
            UsageWriterBatchSize = batchSize,
            UsageWriterFlushIntervalMs = flushIntervalMs,
            UsageWriterMaxFlushRetries = maxRetries,
            UsageWriterMaxPendingEvents = maxPending,
        });

        return new BillingUsageBatchPersistenceHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<BillingUsageBatchPersistenceHandler>.Instance,
            metrics,
            clock);
    }

    private static UsageEvent CreateEvent(string requestId) =>
        new()
        {
            RequestId = requestId,
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
        };
}
