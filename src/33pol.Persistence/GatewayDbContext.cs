using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence;

public sealed class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options)
        : base(options)
    {
    }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();

    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();

    public DbSet<ModelGrantEntity> ModelGrants => Set<ModelGrantEntity>();

    public DbSet<ApiKeyModelGrantEntity> ApiKeyModelGrants => Set<ApiKeyModelGrantEntity>();

    public DbSet<RateCardEntity> RateCards => Set<RateCardEntity>();

    public DbSet<PlanEntity> Plans => Set<PlanEntity>();

    public DbSet<BudgetEntity> Budgets => Set<BudgetEntity>();

    public DbSet<BillingEventEntity> BillingEvents => Set<BillingEventEntity>();

    public DbSet<DailyUsageRollupEntity> DailyUsageRollups => Set<DailyUsageRollupEntity>();

    public DbSet<QuotaAllocationEntity> QuotaAllocations => Set<QuotaAllocationEntity>();

    public DbSet<QuotaUsageEntity> QuotaUsages => Set<QuotaUsageEntity>();

    public DbSet<GatewayStatsSnapshotEntity> GatewayStatsSnapshot => Set<GatewayStatsSnapshotEntity>();

    public DbSet<RecentRequestSnapshotEntity> RecentRequests => Set<RecentRequestSnapshotEntity>();

    public DbSet<QuotaUsageSnapshotEntity> QuotaUsageSnapshots => Set<QuotaUsageSnapshotEntity>();

    public DbSet<ConfigVersionEntity> ConfigVersions => Set<ConfigVersionEntity>();

    public DbSet<CorsSettingsEntity> CorsSettings => Set<CorsSettingsEntity>();

    public DbSet<RateLimitDefaultsEntity> RateLimitDefaults => Set<RateLimitDefaultsEntity>();

    public DbSet<RateLimitPlanEntity> RateLimitPlans => Set<RateLimitPlanEntity>();

    public DbSet<ModelRouteEntity> ModelRoutes => Set<ModelRouteEntity>();

    public DbSet<QuotaSettingsEntity> QuotaSettings => Set<QuotaSettingsEntity>();

    public DbSet<GatewayErrorEntity> GatewayErrors => Set<GatewayErrorEntity>();

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as its UTC tick count (INTEGER) rather than the EF SQLite
    /// default (TEXT). SQLite refuses <c>ORDER BY</c> on DateTimeOffset TEXT columns (it threw 500s on the
    /// admin key list and billing-event queries), and TEXT ordering would sort lexically — silently wrong the
    /// moment a non-UTC offset is written. UtcTicks orders by true instant and keeps range comparisons valid.
    /// All persisted timestamps are UTC instants, so dropping the original offset is intentional and lossless.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Applies to both DateTimeOffset and DateTimeOffset? properties across every entity.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GatewayDbContext).Assembly);
    }

    private sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToUtcTicksConverter()
            : base(offset => offset.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
        {
        }
    }
}
