using Microsoft.Extensions.DependencyInjection;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase5;

public sealed class BillingUsageIdempotencyIntegrationTests
{
    [Fact]
    public async Task PersistAsync_DuplicateRequestId_PersistsSingleEventAndRollup()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var tenantId = Guid.NewGuid();
        var usage = new UsageEvent
        {
            RequestId = "req-idempotent-integration",
            TenantId = tenantId.ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 100,
            CompletionTokens = 50,
            DurationMs = 120,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<BillingUsagePersistenceHandler>();
            await handler.PersistAsync(usage);
            await handler.PersistAsync(new UsageEvent
            {
                RequestId = usage.RequestId,
                TenantId = usage.TenantId,
                ModelId = usage.ModelId,
                PromptTokens = 9_999,
                CompletionTokens = usage.CompletionTokens,
                DurationMs = usage.DurationMs,
                TimestampUtc = usage.TimestampUtc,
            });
        }

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var events = verifyScope.ServiceProvider.GetRequiredService<IBillingEventRepository>();
        var rollups = verifyScope.ServiceProvider.GetRequiredService<IDailyUsageRollupRepository>();

        var storedEvents = await events.QueryAsync(
            new BillingEventQuery(
                TenantId: tenantId,
                Limit: 10));

        storedEvents.Should().ContainSingle();
        storedEvents[0].RequestId.Should().Be("req-idempotent-integration");
        storedEvents[0].PromptTokens.Should().Be(100);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayRollups = await rollups.GetRollupsAsync(today, today, tenantId);
        dayRollups.Should().ContainSingle();
        dayRollups[0].RequestCount.Should().Be(1);
        dayRollups[0].PromptTokens.Should().Be(100);
    }
}
