using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class NoOpBillingServicesTests
{
    [Fact]
    public async Task NoOpBillingUsageService_ReturnsEmptyReportAndEvents()
    {
        var service = new NoOpBillingUsageService();

        var report = await service.GetUsageReportAsync(new UsageReportRequest());
        report.Rollups.Should().BeEmpty();

        var events = await service.QueryEventsAsync(new BillingEventQuery(null, null, null, 10));
        events.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task NoOpBudgetEnforcementService_AlwaysAllows()
    {
        var service = new NoOpBudgetEnforcementService();
        var result = await service.CheckBeforeForwardAsync(Guid.NewGuid().ToString());
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task NoOpUsagePersistenceHandler_CompletesWithoutWork()
    {
        var handler = new NoOpUsagePersistenceHandler();
        await handler.PersistAsync(new UsageEvent
        {
            RequestId = "req-noop",
            ModelId = "m1",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
        });
    }
}
