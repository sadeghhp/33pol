using Microsoft.EntityFrameworkCore;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Configuration;

public sealed class EntityConfigurationTests
{
    [Fact]
    public void Model_HasUniqueTenantSlugIndex()
    {
        using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Model_HasUniqueTenantSlugIndex));
        var model = db.Model;

        var tenant = model.FindEntityType(typeof(Pol33.Persistence.Entities.TenantEntity))!;
        tenant.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Any(p => p.Name == "Slug"));
    }

    [Fact]
    public void Model_MapsIdentityTables()
    {
        using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Model_MapsIdentityTables));
        var model = db.Model;

        model.FindEntityType(typeof(Pol33.Persistence.Entities.TenantEntity))!
            .GetTableName().Should().Be("tenants");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.ApiKeyEntity))!
            .GetTableName().Should().Be("api_keys");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.ModelGrantEntity))!
            .GetTableName().Should().Be("model_grants");
    }

    [Fact]
    public void Model_MapsBillingTables()
    {
        using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Model_MapsBillingTables));
        var model = db.Model;

        model.FindEntityType(typeof(Pol33.Persistence.Entities.RateCardEntity))!
            .GetTableName().Should().Be("rate_cards");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.PlanEntity))!
            .GetTableName().Should().Be("plans");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.BudgetEntity))!
            .GetTableName().Should().Be("budgets");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.BillingEventEntity))!
            .GetTableName().Should().Be("billing_events");

        model.FindEntityType(typeof(Pol33.Persistence.Entities.DailyUsageRollupEntity))!
            .GetTableName().Should().Be("daily_usage_rollups");
    }

    [Fact]
    public void Model_HasUniqueBillingEventRequestIdIndex()
    {
        using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Model_HasUniqueBillingEventRequestIdIndex));
        var model = db.Model;

        var billingEvent = model.FindEntityType(typeof(Pol33.Persistence.Entities.BillingEventEntity))!;
        billingEvent.GetIndexes().Should().Contain(i => i.IsUnique && i.Properties.Any(p => p.Name == "RequestId"));
    }
}
