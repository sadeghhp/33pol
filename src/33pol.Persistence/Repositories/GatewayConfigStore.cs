using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Repositories;

public sealed class GatewayConfigStore(GatewayDbContext dbContext) : IGatewayConfigStore
{
    // The config sections are singleton rows; the fixed keys keep reads and bumps a pure upsert.
    private const int ConfigVersionRowId = 1;
    private const int CorsSettingsRowId = 1;
    private const int RateLimitDefaultsRowId = 1;
    private const int QuotaSettingsRowId = 1;

    public async Task<GatewayConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);

        var cors = await dbContext.CorsSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == CorsSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        var rateLimits = await LoadRateLimitsAsync(cancellationToken).ConfigureAwait(false);

        var quota = await dbContext.QuotaSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == QuotaSettingsRowId, cancellationToken)
            .ConfigureAwait(false);

        return new GatewayConfigSnapshot
        {
            Version = version,
            Cors = new CorsConfigSection
            {
                AllowedOrigins = cors?.AllowedOrigins ?? [],
            },
            RateLimits = rateLimits,
            Quota = quota is null
                ? QuotaConfigSection.Defaults
                : new QuotaConfigSection
                {
                    DefaultMonthlyTokenLimit = quota.DefaultMonthlyTokenLimit,
                    SoftLimitRatio = (double)quota.SoftLimitRatio,
                },
        };
    }

    private async Task<RateLimitsConfigSection> LoadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var defaults = await dbContext.RateLimitDefaults
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == RateLimitDefaultsRowId, cancellationToken)
            .ConfigureAwait(false);

        var plans = await dbContext.RateLimitPlans
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RateLimitsConfigSection
        {
            Default = defaults is null
                ? RateLimitPolicy.Default
                : new RateLimitPolicy(defaults.Rpm, defaults.Burst, defaults.MaxConcurrentStreams),
            Plans = plans.ToDictionary(
                p => p.Slug,
                p => new RateLimitPolicy(p.Rpm, p.Burst, p.MaxConcurrentStreams),
                StringComparer.OrdinalIgnoreCase),
            // TenantOverrides intentionally empty (reserved) — see RateLimitsConfigSection.
        };
    }

    public async Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        return row?.Version ?? 0;
    }

    public async Task<long> IncrementVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ConfigVersions
            .FirstOrDefaultAsync(c => c.Id == ConfigVersionRowId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ConfigVersionEntity { Id = ConfigVersionRowId, Version = 0 };
            dbContext.ConfigVersions.Add(row);
        }

        row.Version += 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Version;
    }
}
