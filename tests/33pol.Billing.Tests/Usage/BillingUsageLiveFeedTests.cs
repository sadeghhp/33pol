using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

/// <summary>
/// The Overview shows a request's input and output cost the moment the usage writer prices it —
/// which is only true if the writer tells the live feed. These pin that hand-off.
/// </summary>
public sealed class BillingUsageLiveFeedTests
{
    private static UsageEvent Event(string requestId, string modelId = "gpt-4o") => new()
    {
        RequestId = requestId,
        ModelId = modelId,
        PromptTokens = 1_000,
        CompletionTokens = 500,
        DurationMs = 100,
        TimestampUtc = DateTimeOffset.UtcNow,
    };

    private static BillingUsagePersistenceHandler CreateHandler(
        IRecentRequestStore feed,
        RateCardRecord? rateCard,
        bool appendSucceeds = true)
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(appendSucceeds);

        var rateCards = Substitute.For<IRateCardRepository>();
        rateCards.GetActiveForModelAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);

        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        return new BillingUsagePersistenceHandler(
            billingEvents,
            Substitute.For<IDailyUsageRollupRepository>(),
            rateCards,
            new RateCardCostCalculator(),
            budgets,
            Substitute.For<IBillingWebhookDispatcher>(),
            new BillingBudgetWarningTracker(),
            new BillingUnpricedModelTracker(),
            Substitute.For<IApiKeyLastUsedTracker>(),
            new BudgetReservationLedger(TimeSpan.FromMinutes(2)),
            NullLogger<BillingUsagePersistenceHandler>.Instance,
            feed);
    }

    private static RateCardRecord RateCard(decimal input, decimal output, string currency = "USD") => new(
        Guid.NewGuid(), "gpt-4o", "GPT-4o", "gpt-4o", input, output, currency,
        DateTimeOffset.UtcNow.AddDays(-1), null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PersistBatch_PublishesPricedInputAndOutputCostToTheLiveFeed()
    {
        var feed = Substitute.For<IRecentRequestStore>();
        var handler = CreateHandler(feed, RateCard(input: 3m, output: 15m));

        await handler.PersistBatchAsync([Event("req-1")]);

        feed.Received(1).AttachUsage("req-1", Arg.Is<RecentRequestUsage>(u =>
            u.PricingStatus == RecentRequestUsage.StatusPriced &&
            u.InputCost == 0.003m &&
            u.OutputCost == 0.0075m &&
            u.TotalCost == 0.0105m &&
            u.Currency == "USD" &&
            u.PromptTokens == 1_000 &&
            u.CompletionTokens == 500));
    }

    [Fact]
    public async Task PersistBatch_WithoutRateCard_ReportsUnpricedRatherThanLeavingTheRowPending()
    {
        var feed = Substitute.For<IRecentRequestStore>();
        var handler = CreateHandler(feed, rateCard: null);

        await handler.PersistBatchAsync([Event("req-1")]);

        feed.Received(1).AttachUsage("req-1", Arg.Is<RecentRequestUsage>(u =>
            u.PricingStatus == RecentRequestUsage.StatusUnpriced &&
            u.TotalCost == null &&
            u.PromptTokens == 1_000));
    }

    [Fact]
    public async Task PersistBatch_DuplicateEvent_StillPublishesSoARetryDoesNotStrandTheRow()
    {
        var feed = Substitute.For<IRecentRequestStore>();
        var handler = CreateHandler(feed, RateCard(3m, 15m), appendSucceeds: false);

        await handler.PersistBatchAsync([Event("req-dup")]);

        feed.Received(1).AttachUsage("req-dup", Arg.Any<RecentRequestUsage>());
    }

    [Fact]
    public async Task PersistBatch_WithoutALiveFeed_StillPersists()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>()).Returns(true);
        var budgets = Substitute.For<IBudgetRepository>();
        budgets.GetByTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        var handler = new BillingUsagePersistenceHandler(
            billingEvents,
            Substitute.For<IDailyUsageRollupRepository>(),
            Substitute.For<IRateCardRepository>(),
            new RateCardCostCalculator(),
            budgets,
            Substitute.For<IBillingWebhookDispatcher>(),
            new BillingBudgetWarningTracker(),
            new BillingUnpricedModelTracker(),
            Substitute.For<IApiKeyLastUsedTracker>(),
            new BudgetReservationLedger(TimeSpan.FromMinutes(2)),
            NullLogger<BillingUsagePersistenceHandler>.Instance);

        await handler.PersistBatchAsync([Event("req-1")]);

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoOpHandler_TellsTheLiveFeedTheRequestWillNeverBePriced()
    {
        var feed = Substitute.For<IRecentRequestStore>();
        var handler = new NoOpUsagePersistenceHandler(feed);

        await handler.PersistAsync(Event("req-1"));

        feed.Received(1).AttachUsage("req-1", Arg.Is<RecentRequestUsage>(u =>
            u.PricingStatus == RecentRequestUsage.StatusUnpriced && u.PromptTokens == 1_000));
    }
}
