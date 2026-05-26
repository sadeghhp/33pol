using Microsoft.EntityFrameworkCore;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Mapping;

public sealed class BillingEntityPersistenceTests
{
    [Fact]
    public async Task SaveBillingEntities_PersistsAllTables()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(SaveBillingEntities_PersistsAllTables));

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = "acme",
            Name = "Acme Corp",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.RateCards.Add(new RateCardEntity
        {
            Id = Guid.NewGuid(),
            Slug = "gpt4o-standard",
            Name = "GPT-4o Standard",
            ModelId = "gpt-4o",
            InputPricePerMillionTokens = 2.50m,
            OutputPricePerMillionTokens = 10.00m,
            Currency = "USD",
            EffectiveFrom = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Plans.Add(new PlanEntity
        {
            Id = Guid.NewGuid(),
            Slug = "enterprise",
            Name = "Enterprise",
            RateCardSlug = "gpt4o-standard",
            MonthlyTokenLimit = 10_000_000,
            RequestsPerMinute = 600,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.Budgets.Add(new BudgetEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Monthly cap",
            AmountLimit = 5000m,
            Currency = "USD",
            WarningThresholdRatio = 0.8m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.BillingEvents.Add(new BillingEventEntity
        {
            Id = Guid.NewGuid(),
            RequestId = "req_abc123",
            TenantId = tenantId,
            ModelId = "gpt-4o-mini",
            CostCenter = "eng-platform",
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalCost = 0.03m,
            DurationMs = 42.5,
            RecordedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        (await db.RateCards.CountAsync()).Should().Be(1);
        (await db.Plans.CountAsync()).Should().Be(1);
        (await db.Budgets.CountAsync()).Should().Be(1);

        var billingEvent = await db.BillingEvents.SingleAsync();
        billingEvent.RequestId.Should().Be("req_abc123");
        billingEvent.CostCenter.Should().Be("eng-platform");
    }
}
