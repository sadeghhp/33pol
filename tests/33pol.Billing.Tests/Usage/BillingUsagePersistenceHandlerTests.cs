using NSubstitute;
using Pol33.Billing.Aggregates;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUsagePersistenceHandlerTests
{
    [Fact]
    public async Task PersistAsync_NewEvent_AppendsAndUpsertsRollup()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        var handler = new BillingUsagePersistenceHandler(
            billingEvents,
            rollups,
            new DailyUsageRollupAggregator());

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-1",
            TenantId = Guid.NewGuid().ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 10,
            CompletionTokens = 5,
            DurationMs = 1,
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        await billingEvents.Received(1).TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>());
        await rollups.Received(1).UpsertRollupsAsync(
            Arg.Is<IReadOnlyList<DailyUsageRollupRecord>>(list => list.Count == 1 && list[0].RequestCount == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_DuplicateRequestId_SkipsRollup()
    {
        var billingEvents = Substitute.For<IBillingEventRepository>();
        billingEvents.TryAppendAsync(Arg.Any<BillingEventRecord>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var handler = new BillingUsagePersistenceHandler(
            billingEvents,
            rollups,
            new DailyUsageRollupAggregator());

        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "dup",
            ModelId = "m1",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
        });

        await rollups.DidNotReceive().UpsertRollupsAsync(Arg.Any<IReadOnlyList<DailyUsageRollupRecord>>(), Arg.Any<CancellationToken>());
    }
}
