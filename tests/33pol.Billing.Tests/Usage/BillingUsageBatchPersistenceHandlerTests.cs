using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Aggregates;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUsageBatchPersistenceHandlerTests
{
    [Fact]
    public async Task PersistAsync_FlushesWhenBatchSizeReached()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups, batchSize: 2);

        await handler.StartAsync(CancellationToken.None);
        await handler.PersistAsync(CreateEvent("req-1"));
        await billingEvents.DidNotReceive().TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
        await handler.PersistAsync(CreateEvent("req-2"));
        await Task.Delay(150);
        await handler.StopAsync(CancellationToken.None);

        await billingEvents.Received(2).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_PeriodicFlush_FlushesAfterInterval()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups, batchSize: 100, flushIntervalMs: 50);

        await handler.StartAsync(CancellationToken.None);
        await handler.PersistAsync(CreateEvent("req-timer"));
        await Task.Delay(120);
        await handler.StopAsync(CancellationToken.None);

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_WhenPersistenceThrows_LogsWithoutPropagating()
    {
        var services = new ServiceCollection();
        services.AddScoped<BillingUsagePersistenceHandler>(_ =>
            throw new InvalidOperationException("persist failed"));
        services.AddSingleton(Options.Create(new BillingOptions
        {
            UsageWriterBatchSize = 1,
            UsageWriterFlushIntervalMs = 60_000,
        }));
        var provider = services.BuildServiceProvider();

        var handler = new BillingUsageBatchPersistenceHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<BillingOptions>>(),
            NullLogger<BillingUsageBatchPersistenceHandler>.Instance);

        await handler.StartAsync(CancellationToken.None);
        var act = async () => await handler.PersistAsync(CreateEvent("req-fail"));
        await act.Should().NotThrowAsync();
        await handler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_FlushesPendingEvents()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups, batchSize: 100, flushIntervalMs: 60_000);

        await handler.StartAsync(CancellationToken.None);
        await handler.PersistAsync(CreateEvent("req-stop"));
        await handler.StopAsync(CancellationToken.None);

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushLoop_FlushesAfterInterval()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = CreateHandler(billingEvents, rollups, batchSize: 100, flushIntervalMs: 50);

        await handler.StartAsync(CancellationToken.None);
        await handler.PersistAsync(CreateEvent("req-interval"));
        await Task.Delay(250);
        await handler.StopAsync(CancellationToken.None);

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    private static BillingUsageBatchPersistenceHandler CreateHandler(
        IBillingEventRepository billingEvents,
        IDailyUsageRollupRepository rollups,
        int batchSize = 100,
        int flushIntervalMs = 1000)
    {
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
        services.AddSingleton<BillingDailyUsageWebhookTracker>();
        services.AddSingleton<IApiKeyLastUsedTracker>(Substitute.For<IApiKeyLastUsedTracker>());
        services.AddSingleton(new BudgetReservationLedger(TimeSpan.FromMinutes(2)));
        services.AddSingleton(Options.Create(new BillingOptions
        {
            UsageWriterBatchSize = batchSize,
            UsageWriterFlushIntervalMs = flushIntervalMs,
        }));

        var provider = services.BuildServiceProvider();

        return new BillingUsageBatchPersistenceHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BillingOptions
            {
                UsageWriterBatchSize = batchSize,
                UsageWriterFlushIntervalMs = flushIntervalMs,
            }),
            NullLogger<BillingUsageBatchPersistenceHandler>.Instance);
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
