using Microsoft.EntityFrameworkCore;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GatewayDbContext).Assembly);
    }
}
